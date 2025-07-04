#region References
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Server.Accounting;
using Server.Diagnostics;
using Server.Gumps;
using Server.HuePickers;
using Server.Items;
using Server.Menus;
#endregion

namespace Server.Network
{
    public interface IPacketEncoder
    {
        void EncodeOutgoingPacket(NetState to, ref byte[] buffer, ref int length);
        void DecodeIncomingPacket(NetState from, ref byte[] buffer, ref int length);
    }

    public interface IPacketEncryptor
    {
        void EncryptOutgoingPacket(NetState to, ref byte[] buffer, ref int length);
        void DecryptIncomingPacket(NetState from, ref byte[] buffer, ref int length);
    }

    public delegate void NetStateCreatedCallback(NetState ns);

    [PropertyObject]
    public class NetState : IComparable<NetState>
    {
        public static bool BufferStaticPackets = false;

        public static event NetStateCreatedCallback CreatedCallback;

        private readonly IPAddress m_Address;
        private ByteQueue m_Buffer;
        private byte[] m_RecvBuffer;
        private readonly SendQueue m_SendQueue;
        private bool m_Running;

        private List<Gump> m_Gumps;
        private List<HuePicker> m_HuePickers;
        private List<IMenu> m_Menus;
        private readonly List<SecureTrade> m_Trades;
        private readonly string m_ToString;
        private ClientVersion m_Version;

        private readonly DateTime m_ConnectedOn;

        [CommandProperty(AccessLevel.Administrator, true)]
        public DateTime ConnectedOn { get { return m_ConnectedOn; } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public TimeSpan ConnectedFor { get { return (DateTime.UtcNow - m_ConnectedOn); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public uint AuthID { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public uint Seed { get; set; }

        [CommandProperty(AccessLevel.Administrator)]
        public IPAddress Address { get { return m_Address; } }

        private static bool m_Paused;

        [Flags]
        private enum AsyncState
        {
            Pending = 0x01,
            Paused = 0x02
        }

        private volatile AsyncState m_AsyncState;
        
		private readonly object m_AsyncLock = new object();

        public IPacketEncoder PacketEncoder { get; set; }
        public IPacketEncryptor PacketEncryptor { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool SentFirstPacket { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool BlockAllPackets { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool? Encrypted { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public ClientFlags Flags { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public ClientVersion Version
        {
            get { return m_Version; }
            set
            {
                m_Version = value;

                if (value >= m_Version70610)
                {
                    _ProtocolChanges = ProtocolChanges.Version70610;
                }
                else if (value >= m_Version70500)
                {
                    _ProtocolChanges = ProtocolChanges.Version70500;
                }
                else if (value >= m_Version704565)
                {
                    _ProtocolChanges = ProtocolChanges.Version704565;
                }
                else if (value >= m_Version70331)
                {
                    _ProtocolChanges = ProtocolChanges.Version70331;
                }
                else if (value >= m_Version70300)
                {
                    _ProtocolChanges = ProtocolChanges.Version70300;
                }
                else if (value >= m_Version70160)
                {
                    _ProtocolChanges = ProtocolChanges.Version70160;
                }
                else if (value >= m_Version70130)
                {
                    _ProtocolChanges = ProtocolChanges.Version70130;
                }
                else if (value >= m_Version7090)
                {
                    _ProtocolChanges = ProtocolChanges.Version7090;
                }
                else if (value >= m_Version7000)
                {
                    _ProtocolChanges = ProtocolChanges.Version7000;
                }
                else if (value >= m_Version60142)
                {
                    _ProtocolChanges = ProtocolChanges.Version60142;
                }
                else if (value >= m_Version6017)
                {
                    _ProtocolChanges = ProtocolChanges.Version6017;
                }
                else if (value >= m_Version6000)
                {
                    _ProtocolChanges = ProtocolChanges.Version6000;
                }
                else if (value >= m_Version502b)
                {
                    _ProtocolChanges = ProtocolChanges.Version502b;
                }
                else if (value >= m_Version500a)
                {
                    _ProtocolChanges = ProtocolChanges.Version500a;
                }
                else if (value >= m_Version407a)
                {
                    _ProtocolChanges = ProtocolChanges.Version407a;
                }
                else if (value >= m_Version400a)
                {
                    _ProtocolChanges = ProtocolChanges.Version400a;
                }
            }
        }

        private static readonly ClientVersion m_Version400a = new ClientVersion("4.0.0a");
        private static readonly ClientVersion m_Version407a = new ClientVersion("4.0.7a");
        private static readonly ClientVersion m_Version500a = new ClientVersion("5.0.0a");
        private static readonly ClientVersion m_Version502b = new ClientVersion("5.0.2b");
        private static readonly ClientVersion m_Version6000 = new ClientVersion("6.0.0.0");
        private static readonly ClientVersion m_Version6017 = new ClientVersion("6.0.1.7");
        private static readonly ClientVersion m_Version60142 = new ClientVersion("6.0.14.2");
        private static readonly ClientVersion m_Version7000 = new ClientVersion("7.0.0.0");
        private static readonly ClientVersion m_Version7090 = new ClientVersion("7.0.9.0");
        private static readonly ClientVersion m_Version70130 = new ClientVersion("7.0.13.0");
        private static readonly ClientVersion m_Version70160 = new ClientVersion("7.0.16.0");
        private static readonly ClientVersion m_Version70300 = new ClientVersion("7.0.30.0");
        private static readonly ClientVersion m_Version70331 = new ClientVersion("7.0.33.1");
        private static readonly ClientVersion m_Version704565 = new ClientVersion("7.0.45.65");
        private static readonly ClientVersion m_Version70500 = new ClientVersion("7.0.50.0");
        private static readonly ClientVersion m_Version70610 = new ClientVersion("7.0.61.0");

        private ProtocolChanges _ProtocolChanges;

        private enum ProtocolChanges
        {
            NewSpellbook = 0x00000001,
            DamagePacket = 0x00000002,
            Unpack = 0x00000004,
            BuffIcon = 0x00000008,
            NewHaven = 0x00000010,
            ContainerGridLines = 0x00000020,
            ExtendedSupportedFeatures = 0x00000040,
            StygianAbyss = 0x00000080,
            HighSeas = 0x00000100,
            NewCharacterList = 0x00000200,
            NewCharacterCreation = 0x00000400,
            ExtendedStatus = 0x00000800,
            NewMobileIncoming = 0x00001000,
            NewSecureTrading = 0x00002000,
            UltimaStore = 0x00004000,
            EndlessJourney = 0x00008000,

            Version400a = NewSpellbook,
            Version407a = Version400a | DamagePacket,
            Version500a = Version407a | Unpack,
            Version502b = Version500a | BuffIcon,
            Version6000 = Version502b | NewHaven,
            Version6017 = Version6000 | ContainerGridLines,
            Version60142 = Version6017 | ExtendedSupportedFeatures,
            Version7000 = Version60142 | StygianAbyss,
            Version7090 = Version7000 | HighSeas,
            Version70130 = Version7090 | NewCharacterList,
            Version70160 = Version70130 | NewCharacterCreation,
            Version70300 = Version70160 | ExtendedStatus,
            Version70331 = Version70300 | NewMobileIncoming,
            Version704565 = Version70331 | NewSecureTrading,
            Version70500 = Version704565 | UltimaStore,
            Version70610 = Version70500 | EndlessJourney
        }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewSpellbook { get { return ((_ProtocolChanges & ProtocolChanges.NewSpellbook) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool DamagePacket { get { return ((_ProtocolChanges & ProtocolChanges.DamagePacket) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool Unpack { get { return ((_ProtocolChanges & ProtocolChanges.Unpack) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool BuffIcon { get { return ((_ProtocolChanges & ProtocolChanges.BuffIcon) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewHaven { get { return ((_ProtocolChanges & ProtocolChanges.NewHaven) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool ContainerGridLines { get { return ((_ProtocolChanges & ProtocolChanges.ContainerGridLines) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool ExtendedSupportedFeatures { get { return ((_ProtocolChanges & ProtocolChanges.ExtendedSupportedFeatures) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool StygianAbyss { get { return ((_ProtocolChanges & ProtocolChanges.StygianAbyss) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool HighSeas { get { return ((_ProtocolChanges & ProtocolChanges.HighSeas) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewCharacterList { get { return ((_ProtocolChanges & ProtocolChanges.NewCharacterList) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewCharacterCreation { get { return ((_ProtocolChanges & ProtocolChanges.NewCharacterCreation) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool ExtendedStatus { get { return ((_ProtocolChanges & ProtocolChanges.ExtendedStatus) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewMobileIncoming { get { return ((_ProtocolChanges & ProtocolChanges.NewMobileIncoming) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool NewSecureTrading { get { return ((_ProtocolChanges & ProtocolChanges.NewSecureTrading) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool UltimaStore { get { return ((_ProtocolChanges & ProtocolChanges.UltimaStore) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool EndlessJourney { get { return ((_ProtocolChanges & ProtocolChanges.EndlessJourney) != 0); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool IsUOTDClient { get { return ((Flags & ClientFlags.UOTD) != 0 || (m_Version != null && m_Version.Type == ClientType.UOTD)); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool IsSAClient { get { return (m_Version != null && m_Version.Type == ClientType.SA); } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool IsEnhancedClient { get { return IsUOTDClient || (m_Version != null && m_Version.Major >= 67); } }

        public List<SecureTrade> Trades { get { return m_Trades; } }

        public void ValidateAllTrades()
        {
            if (m_Trades == null)
            {
                return;
            }

            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i >= m_Trades.Count)
                {
                    continue;
                }

                var trade = m_Trades[i];

                if (trade.From.Mobile.Deleted || trade.To.Mobile.Deleted || !trade.From.Mobile.Alive || !trade.To.Mobile.Alive ||
                    !trade.From.Mobile.InRange(trade.To.Mobile, 2) || trade.From.Mobile.Map != trade.To.Mobile.Map)
                {
                    trade.Cancel();
                }
            }
        }

        public void CancelAllTrades()
        {
            if (m_Trades == null)
            {
                return;
            }

            for (int i = m_Trades.Count - 1; i >= 0; --i)
            {
                if (i < m_Trades.Count)
                {
                    m_Trades[i].Cancel();
                }
            }
        }

        public void RemoveTrade(SecureTrade trade)
        {
            if (m_Trades != null)
            {
                m_Trades.Remove(trade);
            }
        }

        public SecureTrade FindTrade(Mobile m)
        {
            if (m_Trades == null)
            {
                return null;
            }

            foreach (var trade in m_Trades)
            {
                if (trade.From.Mobile == m || trade.To.Mobile == m)
                {
                    return trade;
                }
            }

            return null;
        }

        public SecureTradeContainer FindTradeContainer(Mobile m)
        {
            if (m_Trades == null)
            {
                return null;
            }

            foreach (var trade in m_Trades)
            {
                var from = trade.From;
                var to = trade.To;

                if (from.Mobile == Mobile && to.Mobile == m)
                {
                    return from.Container;
                }

                if (from.Mobile == m && to.Mobile == Mobile)
                {
                    return to.Container;
                }
            }

            return null;
        }

        public SecureTradeContainer AddTrade(NetState state)
        {
            if (m_Trades == null || state.m_Trades == null)
            {
                return null;
            }

            var newTrade = new SecureTrade(Mobile, state.Mobile);

            m_Trades.Add(newTrade);
            state.m_Trades.Add(newTrade);

            return newTrade.From.Container;
        }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool CompressionEnabled { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public int Sequence { get; set; }

        public List<Gump> Gumps { get { return m_Gumps; } }
        public List<HuePicker> HuePickers { get { return m_HuePickers; } }
        public List<IMenu> Menus { get { return m_Menus; } }

        private static int m_GumpCap = 512, m_HuePickerCap = 512, m_MenuCap = 512;

        public static int GumpCap { get { return m_GumpCap; } set { m_GumpCap = value; } }
        public static int HuePickerCap { get { return m_HuePickerCap; } set { m_HuePickerCap = value; } }
        public static int MenuCap { get { return m_MenuCap; } set { m_MenuCap = value; } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public int UpdateRange { get; set; }

        public void WriteConsole(string text)
        {
            Console.WriteLine("Client: {0}: {1}", this, text);
        }

        public void WriteConsole(string format, params object[] args)
        {
            WriteConsole(String.Format(format, args));
        }

        public void AddMenu(IMenu menu)
        {
            if (m_Menus == null)
            {
                m_Menus = new List<IMenu>();
            }

            if (m_Menus.Count < m_MenuCap)
            {
                m_Menus.Add(menu);
            }
            else
            {
                Utility.PushColor(ConsoleColor.Red);
                WriteConsole("Exceeded menu cap, disconnecting...");
                Utility.PopColor();
                Dispose();
            }
        }

        public void RemoveMenu(IMenu menu)
        {
            if (m_Menus != null)
            {
                m_Menus.Remove(menu);
            }
        }

        public void RemoveMenu(int index)
        {
            if (m_Menus != null)
            {
                m_Menus.RemoveAt(index);
            }
        }

        public void ClearMenus()
        {
            if (m_Menus != null)
            {
                m_Menus.Clear();
            }
        }

        public void AddHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers == null)
            {
                m_HuePickers = new List<HuePicker>();
            }

            if (m_HuePickers.Count < m_HuePickerCap)
            {
                m_HuePickers.Add(huePicker);
            }
            else
            {
                Utility.PushColor(ConsoleColor.Red);
                WriteConsole("Exceeded hue picker cap, disconnecting...");
                Utility.PopColor();
                Dispose();
            }
        }

        public void RemoveHuePicker(HuePicker huePicker)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Remove(huePicker);
            }
        }

        public void RemoveHuePicker(int index)
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.RemoveAt(index);
            }
        }

        public void ClearHuePickers()
        {
            if (m_HuePickers != null)
            {
                m_HuePickers.Clear();
            }
        }

        public void AddGump(Gump gump)
        {
            if (m_Gumps == null)
            {
                m_Gumps = new List<Gump>();
            }

            if (m_Gumps.Count < m_GumpCap)
            {
                m_Gumps.Add(gump);
            }
            else
            {
                Utility.PushColor(ConsoleColor.Red);
                WriteConsole("Exceeded gump cap, disconnecting...");
                Utility.PopColor();
                Dispose();
            }
        }

        public void RemoveGump(Gump gump)
        {
            if (m_Gumps != null)
            {
                m_Gumps.Remove(gump);
            }
        }

        public void RemoveGump(int index)
        {
            if (m_Gumps != null)
            {
                m_Gumps.RemoveAt(index);
            }
        }

        public void ClearGumps()
        {
            if (m_Gumps != null)
            {
                m_Gumps.Clear();
            }
        }

        public void LaunchBrowser(string url)
        {
            Send(new MessageLocalized(Serial.MinusOne, -1, MessageType.Label, 0x35, 3, 501231, "", ""));
            Send(new LaunchBrowser(url));
        }

        public CityInfo[] CityInfo { get; set; }

        [CommandProperty(AccessLevel.Administrator, true)]
        public Mobile Mobile { get; set; }

        public ServerInfo[] ServerInfo { get; set; }

        [CommandProperty(AccessLevel.Administrator)]
        public IAccount Account { get; set; }

        public override string ToString()
        {
            return m_ToString;
        }

        private static readonly ConcurrentDictionary<NetState, IPAddress> m_Instances = new ConcurrentDictionary<NetState, IPAddress>();

        public static ReadOnlyCollection<NetState> Instances => (ReadOnlyCollection<NetState>)m_Instances.Keys;
        public static ReadOnlyCollection<IPAddress> Addresses => (ReadOnlyCollection<IPAddress>)m_Instances.Values;

        public const int SendBufferCapacity = 1024, SendBufferSize = 8092;
        public const int ReceiveBufferCapacity = 1024, ReceiveBufferSize = 2048;

        private static readonly BufferPool m_SendBufferPool = new BufferPool("Send", SendBufferCapacity, SendBufferSize);
        private static readonly BufferPool m_ReceiveBufferPool = new BufferPool("Receive", ReceiveBufferCapacity, ReceiveBufferSize);

        public static BufferPool SendBuffers { get { return m_SendBufferPool; } }
        public static BufferPool ReceiveBuffers { get { return m_ReceiveBufferPool; } }

        public NetState(SocketState state)
        {
            Socket = state.Socket;

            m_InitialData = state.Seed;

            m_Buffer = new ByteQueue();

            m_RecvBuffer = m_ReceiveBufferPool.AcquireBuffer();

            m_Gumps = new List<Gump>();
            m_HuePickers = new List<HuePicker>();
            m_Menus = new List<IMenu>();
            m_Trades = new List<SecureTrade>();

            m_SendQueue = new SendQueue();

            m_NextCheckActivity = Core.TickCount + 30000;

            try
            {
                m_Address = Utility.Intern(((IPEndPoint)Socket.RemoteEndPoint).Address);
                m_ToString = m_Address.ToString();
            }
            catch (Exception ex)
            {
                TraceException(ex);
                m_Address = IPAddress.None;
                m_ToString = "(error)";
            }

            m_Instances[this] = m_Address;

            m_ConnectedOn = DateTime.UtcNow;

            UpdateRange = Core.GlobalUpdateRange;

            if (CreatedCallback != null)
            {
                CreatedCallback(this);
            }
        }

        private volatile bool _Sending;

		private readonly object _SendLock = new object();

        public virtual void Send(Packet p)
        {
            if (p == null)
            {
                return;
            }

            try
            {
                if (Socket == null || BlockAllPackets)
                {
                    return;
                }

                var buffer = p.Compile(CompressionEnabled, out int length);

                if (buffer != null)
                {
                    if (buffer.Length <= 0 || length <= 0)
                    {
                        return;
                    }

                    PacketSendProfile prof = null;

                    if (Core.Profiling)
                    {
                        prof = PacketSendProfile.Acquire(p.GetType());
                    }

                    prof?.Start();

                    try
                    {
                        var buffered = false;

                        if (PacketEncoder != null || PacketEncryptor != null)
                        {
                            var packetBuffer = buffer;
                            var packetLength = length;

                            if (BufferStaticPackets && p.State.HasFlag(PacketState.Acquired))
                            {
                                if (packetLength <= SendBufferSize)
                                {
                                    packetBuffer = m_SendBufferPool.AcquireBuffer();
                                }
                                else
                                {
                                    packetBuffer = new byte[packetLength];
                                }

                                System.Buffer.BlockCopy(buffer, 0, packetBuffer, 0, packetLength);
                            }

                            PacketEncoder?.EncodeOutgoingPacket(this, ref packetBuffer, ref packetLength);
                            PacketEncryptor?.EncryptOutgoingPacket(this, ref packetBuffer, ref packetLength);

                            buffered = buffer != packetBuffer && packetBuffer.Length == SendBufferSize;

                            buffer = packetBuffer;
                            length = packetLength;
                        }

                        try
                        {
                            lock (_SendLock)
                            {
                                SendQueue.Gram gram;

                                lock (m_SendQueue)
                                {
                                    gram = m_SendQueue.Enqueue(buffer, length);
                                }

                                if (buffered && m_SendBufferPool.Count < SendBufferCapacity)
                                {
                                    m_SendBufferPool.ReleaseBuffer(buffer);
                                }

                                if (gram != null && !_Sending)
                                {
                                    _Sending = true;

                                    try
                                    {
                                        var segment = new ArraySegment<byte>(gram.Buffer, 0, gram.Length);

                                        _ = Socket.SendAsync(segment, SocketFlags.None).ContinueWith(OnSend);
                                    }
                                    catch (Exception ex)
                                    {
                                        TraceException(ex);
                                        Dispose(false);
                                    }
                                }
                            }
                        }
                        catch (CapacityExceededException)
                        {
                            Utility.PushColor(ConsoleColor.Red);
                            Console.WriteLine("Client: {0}: Too much data pending, disconnecting...", this);
                            Utility.PopColor();

                            Dispose(false);
                        }
                    }
                    finally
                    {
                        prof?.Finish(length);
                    }
                }
                else
                {
                    Utility.PushColor(ConsoleColor.Red);
                    Console.WriteLine("Client: {0}: null buffer send, disconnecting...", this);
                    Utility.PopColor();

                    using (var op = new StreamWriter("null_send.log", true))
                    {
                        op.WriteLine("{0} Client: {1}: null buffer send, disconnecting...", DateTime.UtcNow, this);
                        op.WriteLine(new StackTrace());
                    }

                    Dispose();
                }
            }
            finally
            {
                p.OnSend();
            }
        }

        public void Start()
        {
            m_Running = true;

            if (Socket == null || m_Paused)
            {
                return;
            }

            try
			{
				lock (m_AsyncLock)
	            {
	                if ((m_AsyncState & (AsyncState.Pending | AsyncState.Paused)) == 0)
	                {
	                    InternalBeginReceive();
	                }
	            }
			}
            catch (Exception ex)
            {
                TraceException(ex);
                Dispose(false);
            }
        }

        private byte[] m_InitialData;

        private void InternalBeginReceive()
        {
            m_AsyncState |= AsyncState.Pending;

            if (m_InitialData != null)
            {
                _ = Task.Factory.StartNew(() =>
                {
                    var data = Interlocked.Exchange(ref m_InitialData, null);

                    System.Buffer.BlockCopy(data, 0, m_RecvBuffer, 0, data.Length);

                    return data.Length;
                }).ContinueWith(OnReceive);
            }
            else
            {
                var segment = new ArraySegment<byte>(m_RecvBuffer, 0, m_RecvBuffer.Length);

                _ = Socket.ReceiveAsync(segment, SocketFlags.None).ContinueWith(OnReceive);
            }
        }

        private void OnReceive(Task<int> task)
        {
            try
            {
                if (task.IsFaulted)
                {
                    throw task.Exception;
                }

                var byteCount = task.Result;

                if (byteCount > 0)
                {
                    m_NextCheckActivity = Core.TickCount + 90000;

					byte[] buffer;

					lock (m_AsyncLock)
					{
						buffer = m_RecvBuffer;
					}

                    PacketEncryptor?.DecryptIncomingPacket(this, ref buffer, ref byteCount);
                    PacketEncoder?.DecodeIncomingPacket(this, ref buffer, ref byteCount);

                    lock (m_Buffer)
                    {
                        m_Buffer.Enqueue(buffer, 0, byteCount);
                    }

                    MessagePump.OnReceive(this);

					lock (m_AsyncLock)
					{
	                    m_AsyncState &= ~AsyncState.Pending;
	
	                    if ((m_AsyncState & AsyncState.Paused) == 0)
	                    {
	                        try
	                        {
	                            InternalBeginReceive();
	                        }
	                        catch (Exception ex)
	                        {
	                            TraceException(ex);
	                            Dispose(false);
	                        }
	                    }
	                }
	            }
                else
                {
                    Dispose(false);
                }
            }
            catch
            {
                Dispose(false);
            }
        }

        private void OnSend(Task<int> task)
        {
            try
            {
                if (task.IsFaulted)
                {
                    throw task.Exception;
                }

                var bytes = task.Result;

                if (bytes <= 0)
                {
                    Dispose(false);
                    return;
                }

                m_NextCheckActivity = Core.TickCount + 90000;

                if (m_CoalesceSleep >= 0)
                {
                    Thread.Sleep(m_CoalesceSleep);
                }

                SendQueue.Gram gram;

                lock (m_SendQueue)
                {
                    gram = m_SendQueue.Dequeue();

                    if (gram == null && m_SendQueue.IsFlushReady)
                    {
                        gram = m_SendQueue.CheckFlushReady();
                    }
                }

                if (gram != null)
                {
                    try
                    {
                        var segment = new ArraySegment<byte>(gram.Buffer, 0, gram.Length);

                        _ = Socket.SendAsync(segment, SocketFlags.None).ContinueWith(OnSend);
                    }
                    catch (Exception ex)
                    {
                        TraceException(ex);
                        Dispose(false);
                    }
                }
                else
                {
                    lock (_SendLock)
                    {
                        _Sending = false;
                    }
                }
            }
            catch (Exception)
            {
                Dispose(false);
            }
        }

        public static void Pause()
        {
            m_Paused = true;

            foreach (var ns in m_Instances.Keys)
            {
				lock (ns.m_AsyncLock)
				{
					ns.m_AsyncState |= AsyncState.Paused;
				}
            }
        }

        public static void Resume()
        {
            m_Paused = false;

            foreach (var ns in m_Instances.Keys)
            {
                if (ns.Socket == null)
                {
                    continue;
                }

				lock (ns.m_AsyncLock)
				{
	                ns.m_AsyncState &= ~AsyncState.Paused;
	
	                try
	                {
	                    if ((ns.m_AsyncState & AsyncState.Pending) == 0)
	                    {
	                        ns.InternalBeginReceive();
	                    }
	                }
	                catch (Exception ex)
	                {
	                    TraceException(ex);
	                    ns.Dispose(false);
	                }
	            }
	        }
		}

        public bool Flush()
        {
            if (Socket == null)
            {
                return false;
            }

            lock (_SendLock)
            {
                if (_Sending)
                {
                    return false;
                }

                SendQueue.Gram gram;

                lock (m_SendQueue)
                {
                    if (!m_SendQueue.IsFlushReady)
                    {
                        return false;
                    }

                    gram = m_SendQueue.CheckFlushReady();
                }

                if (gram != null)
                {
                    try
                    {
                        _Sending = true;

                        var segment = new ArraySegment<byte>(gram.Buffer, 0, gram.Length);

                        _ = Socket.SendAsync(segment, SocketFlags.None).ContinueWith(OnSend);

                        return true;
                    }
                    catch (Exception ex)
                    {
                        TraceException(ex);
                        Dispose(false);
                    }
                }
            }

            return false;
        }

        public PacketHandler GetHandler(int packetID)
        {
            if (ContainerGridLines)
            {
                return PacketHandlers.Get6017Handler(packetID);
            }

            return PacketHandlers.GetHandler(packetID);
        }

        public static void FlushAll()
        {
            foreach (NetState ns in m_Instances.Keys)
            {
                _ = ns.Flush();
            }
        }

        private static int m_CoalesceSleep = -1;

        public static int CoalesceSleep { get { return m_CoalesceSleep; } set { m_CoalesceSleep = value; } }

        private long m_NextCheckActivity;

        public void CheckAlive(long curTicks)
        {
            if (Socket == null)
            {
                return;
            }

            if (m_NextCheckActivity - curTicks >= 0)
            {
                return;
            }

            Utility.PushColor(ConsoleColor.Red);
            Console.WriteLine("Client: {0}: Disconnecting due to inactivity...", this);
            Utility.PopColor();

            Dispose();
        }

        public static void TraceException(Exception ex)
        {
            if (!Core.Debug)
            {
                return;
            }

            try
            {
                using (var op = new StreamWriter("network-errors.log", true))
                {
                    op.WriteLine("# {0}", DateTime.UtcNow);

                    op.WriteLine(ex);

                    op.WriteLine();
                    op.WriteLine();
                }
            }
            catch
            { }

            try
            {
                Console.WriteLine(ex);
            }
            catch
            { }
        }

        private bool m_Disposing;

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool IsDisposing { get { return m_Disposing; } }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual void Dispose(bool flush)
        {
            if (Socket == null || m_Disposing)
            {
                return;
            }

            m_Disposing = true;

            if (flush)
            {
                Flush();
            }

            try
            {
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }

            try
            {
                Socket.Close();
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }
            
            var buffer = Interlocked.Exchange(ref m_RecvBuffer, null);

            if (buffer != null)
            {
                m_ReceiveBufferPool.ReleaseBuffer(buffer);
            }

            Socket = null;

            PacketEncoder = null;
            PacketEncryptor = null;

            m_Buffer = null;

            m_Running = false;

            m_Disposed.Enqueue(this);
        }

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromMinutes(1.0), TimeSpan.FromMinutes(1.5), CheckAllAlive);
        }

        public static void CheckAllAlive()
        {
            try
            {
                var curTicks = Core.TickCount;

                foreach (var ns in m_Instances.Keys)
                {
                    ns.CheckAlive(curTicks);
                }
            }
            catch (Exception ex)
            {
                TraceException(ex);
            }
        }

        private static readonly ConcurrentQueue<NetState> m_Disposed = new ConcurrentQueue<NetState>();

        public static void ProcessDisposedQueue()
        {
            var breakout = m_Disposed.Count;

            while (--breakout >= 0 && m_Disposed.TryDequeue(out var ns))
            {
                var m = ns.Mobile;
                var a = ns.Account;

                if (m != null)
                {
                    m.CloseAllGumps();

                    m.NetState = null;
                    ns.Mobile = null;
                }

                m_Instances.TryRemove(ns, out _);

                ns.m_Gumps.Clear();
                ns.m_Menus.Clear();
                ns.m_HuePickers.Clear();

                ns.Account = null;
                ns.ServerInfo = null;
                ns.CityInfo = null;

                if (!ns.m_SendQueue.IsEmpty)
                {
                    ns.m_SendQueue.Clear();
                }

                Utility.PushColor(ConsoleColor.Red);

                if (a != null)
                {
                    ns.WriteConsole("Disconnected. [{0} Online] [{1}]", m_Instances.Count, a);
                }
                else if (MessagePump.Display(ns))
                {
                    ns.WriteConsole("Disconnected. [{0} Online]", m_Instances.Count);
                }

                Utility.PopColor();
            }
        }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool Running { get { return m_Running; } }

        [CommandProperty(AccessLevel.Administrator, true)]
        public bool Seeded { get; set; }

        public Socket Socket { get; private set; }

        public ByteQueue Buffer { get { return m_Buffer; } }

        public ExpansionInfo ExpansionInfo
        {
            get
            {
                for (var i = ExpansionInfo.Table.Length - 1; i >= 0; i--)
                {
                    var info = ExpansionInfo.Table[i];

                    if ((info.RequiredClient != null && Version >= info.RequiredClient) || ((Flags & info.ClientFlags) != 0))
                    {
                        return info;
                    }
                }

                return ExpansionInfo.GetInfo(Expansion.None);
            }
        }

        [CommandProperty(AccessLevel.Administrator, true)]
        public Expansion Expansion { get { return (Expansion)ExpansionInfo.ID; } }

        public bool SupportsExpansion(ExpansionInfo info, bool checkCoreExpansion)
        {
            if (info == null || (checkCoreExpansion && (int)Core.Expansion < info.ID))
            {
                return false;
            }

            if (info.RequiredClient != null)
            {
                return (IsEnhancedClient || Version >= info.RequiredClient);
            }

            return ((Flags & info.ClientFlags) != 0);
        }

        public bool SupportsExpansion(Expansion ex, bool checkCoreExpansion)
        {
            return SupportsExpansion(ExpansionInfo.GetInfo(ex), checkCoreExpansion);
        }

        public bool SupportsExpansion(Expansion ex)
        {
            return SupportsExpansion(ex, true);
        }

        public bool SupportsExpansion(ExpansionInfo info)
        {
            return SupportsExpansion(info, true);
        }

        public int CompareTo(NetState other)
        {
            if (other == null)
            {
                return 1;
            }

            return String.Compare(m_ToString, other.m_ToString, StringComparison.Ordinal);
        }

        #region Packet Throttling

        private readonly long[] _Throttles = new long[Byte.MaxValue];

        public void SetPacketTime(byte packetID)
        {
            _Throttles[packetID] = Core.TickCount;
        }

        public long GetPacketTime(byte packetID)
        {
            return _Throttles[packetID];
        }

        public bool IsThrottled(byte packetID, int delayMS)
        {
            return _Throttles[packetID] + delayMS > Core.TickCount;
        }

        #endregion
    }
}
