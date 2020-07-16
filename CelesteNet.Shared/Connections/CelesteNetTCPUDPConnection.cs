﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Celeste.Mod.CelesteNet.DataTypes;
using Celeste.Mod.Helpers;
using Monocle;

namespace Celeste.Mod.CelesteNet {
    public class CelesteNetTCPUDPConnection : CelesteNetConnection {

        public TcpClient TCP;
        public PositionAwareStream<NetworkStream> TCPStream;
        public BinaryReader TCPReader;
        public BinaryWriter TCPWriter;

        public UdpClient? UDP;
        public bool SendUDP = true;

        protected ConcurrentBag<BufferHelper> BufferHelpers = new ConcurrentBag<BufferHelper>();

        protected Thread? ReadTCPThread;
        protected Thread? ReadUDPThread;

        public override bool IsConnected => TCP?.Connected ?? false;
        public override string ID => "TCP" + (UDP != null ? "/UDP" : "only") + " " + (TCPRemoteEndPoint?.ToString() ?? $"?{GetHashCode()}");
        public override string UID => $"tcpudp-{TCPRemoteEndPoint?.Address?.ToString() ?? "unknown"}";

        protected IPEndPoint? TCPLocalEndPoint;
        protected IPEndPoint? TCPRemoteEndPoint;
        public IPEndPoint? UDPLocalEndPoint;
        public IPEndPoint? UDPRemoteEndPoint;

        public readonly CelesteNetSendQueue TCPQueue;
        public readonly CelesteNetSendQueue UDPQueue;

        private readonly object UDPErrorLock = new object();
        private Exception UDPErrorLast;
        private Action<CelesteNetTCPUDPConnection, Exception, bool>? _OnUDPError;
        public event Action<CelesteNetTCPUDPConnection, Exception, bool> OnUDPError {
            add {
                lock (UDPErrorLock) {
                    _OnUDPError += value;
                    if (UDPErrorLast != null)
                        value?.Invoke(this, UDPErrorLast, false);
                }
            }
            remove {
                lock (UDPErrorLock) {
                    _OnUDPError -= value;
                }
            }
        }

#pragma warning disable CS8618 // Every other ctor uses this ctor and initializes everything properly.
        private CelesteNetTCPUDPConnection(DataContext data)
#pragma warning restore CS8618
            : base(data) {

            TCPQueue = DefaultSendQueue;
            TCPQueue.SendKeepAliveUpdate = false;
            SendQueues.Add(UDPQueue = new CelesteNetSendQueue(this) {
                SendKeepAliveUpdate = true
            });
        }

        public CelesteNetTCPUDPConnection(DataContext data, string host, int port, bool canUDP)
            : this(data) {
            TcpClient tcp = new TcpClient(host, port);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 3000);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 3000);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);

            UdpClient? udp = null;
            if (canUDP) {
                // Reuse TCP endpoint as - at least on Windows - TCP and UDP hostname
                // lookups can result in IPv4 for TCP vs IPv6 for UDP in some cases.
                udp = tcp.Client.RemoteEndPoint is IPEndPoint tcpEP ?
                    new UdpClient(tcpEP.Address.ToString(), tcpEP.Port) :
                    new UdpClient(host, port);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 3000);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 3000);
            }

            InitTCPUDP(tcp, udp);
        }

        public CelesteNetTCPUDPConnection(DataContext data, TcpClient tcp, UdpClient? udp)
            : this(data) {
            InitTCPUDP(tcp, udp);
        }

        private void InitTCPUDP(TcpClient tcp, UdpClient? udp) {
            TCP = tcp;
            TCPStream = new PositionAwareStream<NetworkStream>(tcp.GetStream());
            TCPReader = new BinaryReader(TCPStream, Encoding.UTF8, true);
            TCPWriter = new BinaryWriter(TCPStream, Encoding.UTF8, true);

            UDP = udp;
        }

        public void StartReadTCP() {
            if (TCP == null || ReadTCPThread != null)
                return;

            TCPLocalEndPoint = (IPEndPoint) TCP.Client.LocalEndPoint;
            TCPRemoteEndPoint = (IPEndPoint) TCP.Client.RemoteEndPoint;

            ReadTCPThread = new Thread(ReadTCPLoop) {
                Name = $"{GetType().Name} ReadTCP ({Creator} - {GetHashCode()})",
                IsBackground = true
            };
            ReadTCPThread.Start();
        }

        public void StartReadUDP() {
            if (UDP == null || ReadUDPThread != null)
                return;

            UDPLocalEndPoint = (IPEndPoint) UDP.Client.LocalEndPoint;
            try {
                UDPRemoteEndPoint = (IPEndPoint) UDP.Client.RemoteEndPoint;
            } catch (Exception) {
                UDPRemoteEndPoint = TCPRemoteEndPoint;
            }

            ReadUDPThread = new Thread(ReadUDPLoop) {
                Name = $"{GetType().Name} ReadUDP ({Creator} - {GetHashCode()})",
                IsBackground = true
            };
            ReadUDPThread.Start();
        }

        public bool SendViaUDP(DataType data)
            => (data.DataFlags & DataFlags.Update) == DataFlags.Update && UDP != null && SendUDP;

        public override CelesteNetSendQueue GetQueue(DataType data) {
            if (SendViaUDP(data))
                return UDPQueue;
            return TCPQueue;
        }

        public override void SendRaw(DataType data) {
            // Let's have some fun with dumb port sniffers.
            if (data is DataTCPHTTPTeapot teapot) {
                WriteTeapot(teapot.ConnectionToken);
                return;
            }

            if (data is DataUDPConnectionToken token) {
                WriteToken(token.Value);
                return;
            }

            if (!BufferHelpers.TryTake(out BufferHelper buffer))
                buffer = new BufferHelper();

            try {
                buffer.Stream.Seek(0, SeekOrigin.Begin);

                int length = Data.Write(buffer.Writer, data);
                byte[] raw = buffer.Stream.GetBuffer();

                if (SendViaUDP(data) && UDP != null) {
                    // Missed updates aren't that bad...
                    // Make sure that we have a default address if sending it without an endpoint
                    // UDP is a mess and the UdpClient can be shared.
                    // UDP.Client.Connected is true on mono server...
                    try {
                        if (UDP.Client.Connected && ReadUDPThread != null) {
                            UDP.Send(raw, length);
                        } else if (UDPRemoteEndPoint != null) {
                            UDP.Send(raw, length, UDPRemoteEndPoint);
                        }
                    } catch (Exception e) {
                        lock (UDPErrorLock) {
                            UDPErrorLast = e;
                            if (_OnUDPError != null) {
                                _OnUDPError(this, e, false);
                            } else {
                                Logger.Log(LogLevel.CRI, "tcpudpcon", $"UDP send failure:\n{this}\n{e}");
                            }
                        }
                    }

                } else {
                    lock (TCPWriter) // This can be theoretically reached from the UDP queue.
                        TCPWriter.Write(raw, 0, length);
                }
            } finally {
                lock (DisposeLock) {
                    BufferHelpers.Add(buffer);
                    if (!IsAlive)
                        buffer.Dispose();
                }
            }
        }

        public uint ReadTeapot() {
            uint token = 0;
            using (StreamReader reader = new StreamReader(TCPStream, Encoding.UTF8, false, 1024, true)) {
                for (string line; !string.IsNullOrWhiteSpace(line = reader?.ReadLine() ?? "");) {
                    if (line.StartsWith(CelesteNetUtils.HTTPTeapotConToken)) {
                        token = uint.Parse(line.Substring(CelesteNetUtils.HTTPTeapotConToken.Length).Trim());
                    }
                }
            }
            return token;
        }

        public void WriteTeapot(uint token) {
            using (StreamWriter writer = new StreamWriter(TCPStream, Encoding.UTF8, 1024, true))
                writer.Write(string.Format(CelesteNetUtils.HTTPTeapot, token));
            TCPStream.Flush();
        }

        public void WriteToken(uint token) {
            if (UDP == null)
                return;
            if (UDP.Client.Connected && ReadUDPThread != null) {
                UDP.Send(BitConverter.GetBytes(token), 4);
            } else if (UDPRemoteEndPoint != null) {
                UDP.Send(BitConverter.GetBytes(token), 4, UDPRemoteEndPoint);
            }
        }

        protected virtual void ReadTCPLoop() {
            try {
                while ((TCP?.Connected ?? false) && IsAlive) {
                    Receive(Data.Read(TCPReader));
                }

            } catch (ThreadAbortException) {

            } catch (Exception e) {
                if (!IsAlive)
                    return;

                Logger.Log(LogLevel.CRI, "tcpudpcon", $"TCP loop error:\n{this}\n{(e is ObjectDisposedException ? "Disposed" : e is IOException ? e.Message : e.ToString())}");
                ReadTCPThread = null;
                Dispose();
                return;
            }
        }

        protected virtual void ReadUDPLoop() {
            try {
                using (MemoryStream stream = new MemoryStream())
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8)) {
                    while (UDP != null && IsAlive) {
                        IPEndPoint? remote = null;
                        byte[] raw = UDP.Receive(ref remote);
                        if (UDPRemoteEndPoint != null && !remote.Equals(UDPRemoteEndPoint))
                            continue;

                        stream.Seek(0, SeekOrigin.Begin);
                        stream.Write(raw, 0, raw.Length);

                        stream.Seek(0, SeekOrigin.Begin);
                        Receive(Data.Read(reader));
                    }
                }

            } catch (ThreadAbortException) {

            } catch (Exception e) {
                if (!IsAlive)
                    return;

                ReadUDPThread = null;
                lock (UDPErrorLock) {
                    UDPErrorLast = e;
                    if (_OnUDPError != null) {
                        _OnUDPError(this, e, true);
                    } else {
                        Logger.Log(LogLevel.CRI, "tcpudpcon", $"UDP loop error:\n{this}\n{(e is ObjectDisposedException ? "Disposed" : e is SocketException ? e.Message : e.ToString())}");
                        Dispose();
                    }
                }
                return;
            }
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);

            try {
                TCP.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 0);
                TCP.Client.Disconnect(false);
            } catch (Exception) {
            }
            TCPReader.Dispose();
            TCPWriter.Dispose();
            TCPStream.Dispose();
            TCP.Close();

            // UDP is a mess and the UdpClient can be shared.
            if (ReadUDPThread != null) {
                try {
                    UDP?.Client?.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 0);
                } catch (Exception) {
                }
                UDP?.Close();
            }

            foreach (BufferHelper buffer in BufferHelpers)
                buffer.Dispose();

        }

        public override string ToString() {
            string s = $"CelesteNetTCPUDPConnection {TCPLocalEndPoint?.ToString() ?? "???"} <-> {TCPRemoteEndPoint?.ToString() ?? "???"}";
            if (UDPRemoteEndPoint != null)
                s += $" / {UDPLocalEndPoint?.ToString() ?? "???"} <-> {UDPRemoteEndPoint?.ToString() ?? "???"}";
            return s;
        }

        protected class BufferHelper : IDisposable {

            public MemoryStream Stream;
            public BinaryWriter Writer;

            public BufferHelper() {
                Stream = new MemoryStream();
                Writer = new BinaryWriter(Stream, Encoding.UTF8);
            }

            public void Dispose() {
                Writer?.Dispose();
                Stream?.Dispose();
            }

        }

    }
}
