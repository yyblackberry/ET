﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ETModel
{
	public struct WaitSendBuffer
	{
		public byte[] Bytes;
		public int Length;

		public WaitSendBuffer(byte[] bytes, int length)
		{
			this.Bytes = bytes;
			this.Length = length;
		}
	}

	public class KChannel : AChannel
	{
		private Socket socket;

		private IntPtr kcp;

		private readonly Queue<WaitSendBuffer> sendBuffer = new Queue<WaitSendBuffer>();

		private bool isConnected;
		
		private readonly IPEndPoint remoteEndPoint;

		public uint lastRecvTime;
		
		public uint CreateTime;

		public uint RemoteConn;

		public Packet packet = new Packet(ushort.MaxValue);

		// accept
		public KChannel(uint localConn, uint remoteConn, Socket socket, IPEndPoint remoteEndPoint, KService kService) : base(kService, ChannelType.Accept)
		{
			this.InstanceId = IdGenerater.GenerateId();

			this.LocalConn = localConn;
			this.RemoteConn = remoteConn;
			this.remoteEndPoint = remoteEndPoint;
			this.socket = socket;
			this.kcp = Kcp.KcpCreate(this.RemoteConn, new IntPtr(this.LocalConn));
			Kcp.KcpSetoutput(
				this.kcp,
				(bytes, len, k, user) =>
				{
					KService.Output(bytes, len, user);
					return len;
				}
			);
			Kcp.KcpNodelay(this.kcp, 1, 10, 1, 1);
			Kcp.KcpWndsize(this.kcp, 256, 256);
			Kcp.KcpSetmtu(this.kcp, 470);
			this.lastRecvTime = kService.TimeNow;
			this.CreateTime = kService.TimeNow;
			this.Accept();
		}

		// connect
		public KChannel(uint localConn, Socket socket, IPEndPoint remoteEndPoint, KService kService) : base(kService, ChannelType.Connect)
		{
			this.InstanceId = IdGenerater.GenerateId();

			this.LocalConn = localConn;
			this.socket = socket;
			this.remoteEndPoint = remoteEndPoint;
			this.lastRecvTime = kService.TimeNow;
			this.CreateTime = kService.TimeNow;
			this.Connect();
		}

		public uint LocalConn
		{
			get
			{
				return (uint)this.Id;
			}
			set
			{
				this.Id = value;
			}
		}

		public override void Dispose()
		{
			if (this.IsDisposed)
			{
				return;
			}

			base.Dispose();

			try
			{
				if (this.Error == ErrorCode.ERR_Success)
				{
					for (int i = 0; i < 4; i++)
					{
						this.Disconnect();
					}
				}
			}
			catch (Exception)
			{
			}

			if (this.kcp != IntPtr.Zero)
			{
				Kcp.KcpRelease(this.kcp);
				this.kcp = IntPtr.Zero;
			}
			this.socket = null;
		}

		public override MemoryStream Stream
		{
			get
			{
				return this.packet.Stream;
			}
		}

		public void Disconnect(int error)
		{
			this.OnError(error);
		}

		private KService GetService()
		{
			return (KService)this.service;
		}

		public void HandleConnnect(uint remoteConn)
		{
			if (this.isConnected)
			{
				return;
			}

			this.RemoteConn = remoteConn;

			this.kcp = Kcp.KcpCreate(this.RemoteConn, new IntPtr(this.LocalConn));
			Kcp.KcpSetoutput(
				this.kcp,
				(bytes, len, k, user) =>
				{
					KService.Output(bytes, len, user);
					return len;
				}
			);
			Kcp.KcpNodelay(this.kcp, 1, 10, 1, 1);
			Kcp.KcpWndsize(this.kcp, 256, 256);
			Kcp.KcpSetmtu(this.kcp, 470);

			this.isConnected = true;
			this.lastRecvTime = this.GetService().TimeNow;

			HandleSend();
		}

		public void Accept()
		{
			if (this.socket == null)
			{
				return;
			}
			
			uint timeNow = this.GetService().TimeNow;

			try
			{
				this.packet.Bytes.WriteTo(0, KcpProtocalType.ACK);
				this.packet.Bytes.WriteTo(1, LocalConn);
				this.packet.Bytes.WriteTo(5, RemoteConn);
				this.socket.SendTo(this.packet.Bytes, 0, 9, SocketFlags.None, remoteEndPoint);
				
				// 200毫秒后再次update发送connect请求
				this.GetService().AddToUpdateNextTime(timeNow + 200, this.Id);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		/// <summary>
		/// 发送请求连接消息
		/// </summary>
		private void Connect()
		{
			try
			{
				uint timeNow = this.GetService().TimeNow;
				
				this.lastRecvTime = timeNow;
				
				this.packet.Bytes.WriteTo(0, KcpProtocalType.SYN);
				this.packet.Bytes.WriteTo(1, this.LocalConn);
				this.socket.SendTo(this.packet.Bytes, 0, 5, SocketFlags.None, remoteEndPoint);
				
				// 200毫秒后再次update发送connect请求
				this.GetService().AddToUpdateNextTime(timeNow + 300, this.Id);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		private void Disconnect()
		{
			if (this.socket == null)
			{
				return;
			}
			try
			{
				this.packet.Bytes.WriteTo(0, KcpProtocalType.FIN);
				this.packet.Bytes.WriteTo(1, this.LocalConn);
				this.packet.Bytes.WriteTo(5, this.RemoteConn);
				this.packet.Bytes.WriteTo(9, (uint)this.Error);
				this.socket.SendTo(this.packet.Bytes, 0, 13, SocketFlags.None, remoteEndPoint);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		public void Update()
		{
			if (this.IsDisposed)
			{
				return;
			}

			uint timeNow = this.GetService().TimeNow;
			
			// 如果还没连接上，发送连接请求
			if (!this.isConnected)
			{
				// 10秒没连接上则报错
				if (timeNow - this.CreateTime > 10 * 1000)
				{
					this.OnError(ErrorCode.ERR_KcpCantConnect);
					return;
				}
				
				if (timeNow - this.lastRecvTime < 200)
				{
					return;
				}

				switch (ChannelType)
				{
					case ChannelType.Accept:
						this.Accept();
						break;
					case ChannelType.Connect:
						this.Connect();
						break;
				}
				
				return;
			}

			// 超时断开连接
			if (timeNow - this.lastRecvTime > 40 * 1000)
			{
				this.OnError(ErrorCode.ERR_KcpChannelTimeout);
				return;
			}

			try
			{
				Kcp.KcpUpdate(this.kcp, timeNow);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketError);
				return;
			}


			if (this.kcp != IntPtr.Zero)
			{
				uint nextUpdateTime = Kcp.KcpCheck(this.kcp, timeNow);
				this.GetService().AddToUpdateNextTime(nextUpdateTime, this.Id);
			}
		}

		private void HandleSend()
		{
			while (true)
			{
				if (this.sendBuffer.Count <= 0)
				{
					break;
				}

				WaitSendBuffer buffer = this.sendBuffer.Dequeue();
				this.KcpSend(buffer.Bytes, buffer.Length);
			}
		}

		public void HandleRecv(byte[] date, int offset, int length)
		{
			if (this.IsDisposed)
			{
				return;
			}

			this.isConnected = true;
			
			Kcp.KcpInput(this.kcp, date, offset, length);
			this.GetService().AddToUpdateNextTime(0, this.Id);

			while (true)
			{
				if (this.IsDisposed)
				{
					return;
				}
				int n = Kcp.KcpPeeksize(this.kcp);
				if (n < 0)
				{
					return;
				}
				if (n == 0)
				{
					this.OnError((int)SocketError.NetworkReset);
					return;
				}

				byte[] buffer = this.packet.Bytes;
				this.packet.Stream.SetLength(n);
				this.packet.Stream.Seek(0, SeekOrigin.Begin);
				int count = Kcp.KcpRecv(this.kcp, buffer, ushort.MaxValue);
				if (n != count)
				{
					return;
				}
				if (count <= 0)
				{
					return;
				}

				this.lastRecvTime = this.GetService().TimeNow;

				this.OnRead(packet);
			}
		}

		public override void Start()
		{
		}

		public void Output(IntPtr bytes, int count)
		{
			if (this.IsDisposed)
			{
				return;
			}
			try
			{
				if (count == 0)
				{
					Log.Error($"output 0");
					return;
				}

				this.packet.Bytes.WriteTo(0, KcpProtocalType.MSG);
				// 每个消息头部写下该channel的id;
				this.packet.Bytes.WriteTo(1, this.LocalConn);
				Marshal.Copy(bytes, this.packet.Bytes, 5, count);
				this.socket.SendTo(this.packet.Bytes, 0, count + 5, SocketFlags.None, this.remoteEndPoint);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		private void KcpSend(byte[] buffers, int length)
		{
			if (this.IsDisposed)
			{
				return;
			}
			Kcp.KcpSend(this.kcp, buffers, length);
			this.GetService().AddToUpdateNextTime(0, this.Id);
		}

		private void Send(byte[] buffer, int index, int length)
		{
			if (isConnected)
			{
				this.KcpSend(buffer, length);
				return;
			}

			this.sendBuffer.Enqueue(new WaitSendBuffer(buffer, length));
		}

		public override void Send(MemoryStream stream)
		{
			ushort size = (ushort)(stream.Length - stream.Position);
			byte[] bytes;
			if (this.isConnected)
			{
				bytes = stream.GetBuffer();
			}
			else
			{
				bytes = new byte[size];
				Array.Copy(stream.GetBuffer(), stream.Position, bytes, 0, size);
			}

			Send(bytes, 0, size);
		}
	}
}
