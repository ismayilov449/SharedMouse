using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SharedMouse
{
    /// <summary>
    /// Bluetooth RFCOMM ("serial port over Bluetooth") through plain Windows sockets.
    /// The second laptop listens and advertises our service id; the main laptop connects to it by that id.
    /// </summary>
    static class Bluetooth
    {
        public static readonly Guid ServiceId = new Guid("6f1e9a4c-2d3b-4c8e-9a71-5b0d3e2f8c41");

        const AddressFamily AF_BTH = (AddressFamily)32;
        const ProtocolType BTHPROTO_RFCOMM = (ProtocolType)3;
        const SocketOptionLevel SOL_RFCOMM = (SocketOptionLevel)3;
        const SocketOptionName SO_BTH_ENCRYPT = (SocketOptionName)2;

        public struct Device
        {
            public ulong Address;
            public string Name;
        }

        /// <summary>Paired devices whose Bluetooth class says "computer".</summary>
        public static List<Device> PairedComputers()
        {
            var result = new List<Device>();
            var search = new Native.BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = Marshal.SizeOf<Native.BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                fReturnAuthenticated = 1,
                fReturnRemembered = 1,
                fReturnConnected = 1,
            };
            var info = new Native.BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<Native.BLUETOOTH_DEVICE_INFO>() };

            IntPtr find = Native.BluetoothFindFirstDevice(ref search, ref info);
            if (find == IntPtr.Zero)
                return result;
            try
            {
                do
                {
                    bool isComputer = ((info.ulClassofDevice >> 8) & 0x1F) == 1;
                    if (isComputer && result.TrueForAll(d => d.Address != info.Address))
                        result.Add(new Device { Address = info.Address, Name = info.szName });
                    info.dwSize = Marshal.SizeOf<Native.BLUETOOTH_DEVICE_INFO>();
                } while (Native.BluetoothFindNextDevice(find, ref info));
            }
            finally
            {
                Native.BluetoothFindDeviceClose(find);
            }
            return result;
        }

        public static Socket Connect(ulong address)
        {
            var s = new Socket(AF_BTH, SocketType.Stream, BTHPROTO_RFCOMM);
            try
            {
                s.SetSocketOption(SOL_RFCOMM, SO_BTH_ENCRYPT, true);
                // Port 0 = let Windows look up the RFCOMM channel of our service id on the other laptop.
                s.Connect(new BtEndPoint(address, ServiceId, 0));
                return s;
            }
            catch
            {
                s.Close();
                throw;
            }
        }

        /// <summary>Starts listening and advertises the service so the main laptop can find it.</summary>
        public static Socket Listen()
        {
            var s = new Socket(AF_BTH, SocketType.Stream, BTHPROTO_RFCOMM);
            try
            {
                // Only paired devices can connect, and the link is encrypted.
                s.SetSocketOption(SOL_RFCOMM, SO_BTH_ENCRYPT, true);
                s.Bind(new BtEndPoint(0, Guid.Empty, BtEndPoint.PortAny));
                s.Listen(1);
                Advertise((BtEndPoint)s.LocalEndPoint);
                return s;
            }
            catch
            {
                s.Close();
                throw;
            }
        }

        static void Advertise(BtEndPoint local)
        {
            byte[] sockaddr = BtEndPoint.ToBytes(local);
            IntPtr addr = Marshal.AllocHGlobal(sockaddr.Length);
            IntPtr guid = Marshal.AllocHGlobal(16);
            IntPtr name = Marshal.StringToHGlobalUni("SharedMouse");
            IntPtr csaddr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.CSADDR_INFO>());
            try
            {
                Marshal.Copy(sockaddr, 0, addr, sockaddr.Length);
                Marshal.Copy(ServiceId.ToByteArray(), 0, guid, 16);
                var sa = new Native.SOCKET_ADDRESS { lpSockaddr = addr, iSockaddrLength = sockaddr.Length };
                Marshal.StructureToPtr(new Native.CSADDR_INFO
                {
                    LocalAddr = sa,
                    RemoteAddr = sa,
                    iSocketType = (int)SocketType.Stream,
                    iProtocol = (int)BTHPROTO_RFCOMM,
                }, csaddr, false);

                var qs = new Native.WSAQUERYSET
                {
                    dwSize = Marshal.SizeOf<Native.WSAQUERYSET>(),
                    lpszServiceInstanceName = name,
                    lpServiceClassId = guid,
                    dwNameSpace = Native.NS_BTH,
                    dwNumberOfCsAddrs = 1,
                    lpcsaBuffer = csaddr,
                };
                if (Native.WSASetServiceW(ref qs, Native.RNRSERVICE_REGISTER, 0) != 0)
                    throw new SocketException(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(addr);
                Marshal.FreeHGlobal(guid);
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(csaddr);
            }
        }

        public static string FormatAddress(ulong a)
        {
            var parts = new string[6];
            for (int i = 0; i < 6; i++)
                parts[i] = ((a >> (8 * (5 - i))) & 0xFF).ToString("X2");
            return string.Join(":", parts);
        }

        public static ulong ParseAddress(string s)
        {
            string hex = s.Replace(":", "").Replace("-", "");
            if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong a))
                throw new ArgumentException("Bluetooth address must look like 00:11:22:33:44:55");
            return a;
        }

        /// <summary>SOCKADDR_BTH: family(2) + btAddr(8) + serviceClassId(16) + port(4), packed.</summary>
        sealed class BtEndPoint : EndPoint
        {
            public const uint PortAny = 0xFFFFFFFF;
            const int Size = 30;

            readonly ulong address;
            readonly Guid service;
            readonly uint port;

            public BtEndPoint(ulong address, Guid service, uint port)
            {
                this.address = address;
                this.service = service;
                this.port = port;
            }

            public override AddressFamily AddressFamily => AF_BTH;

            public override SocketAddress Serialize()
            {
                byte[] b = ToBytes(this);
                var sa = new SocketAddress(AF_BTH, Size);
                for (int i = 2; i < Size; i++)
                    sa[i] = b[i];
                return sa;
            }

            public override EndPoint Create(SocketAddress sa)
            {
                var b = new byte[Size];
                for (int i = 0; i < Size && i < sa.Size; i++)
                    b[i] = sa[i];
                var g = new byte[16];
                Array.Copy(b, 10, g, 0, 16);
                return new BtEndPoint(BitConverter.ToUInt64(b, 2), new Guid(g), BitConverter.ToUInt32(b, 26));
            }

            public static byte[] ToBytes(BtEndPoint ep)
            {
                var b = new byte[Size];
                BitConverter.GetBytes((ushort)AF_BTH).CopyTo(b, 0);
                BitConverter.GetBytes(ep.address).CopyTo(b, 2);
                ep.service.ToByteArray().CopyTo(b, 10);
                BitConverter.GetBytes(ep.port).CopyTo(b, 26);
                return b;
            }

            public override string ToString() => $"{FormatAddress(address)} channel {port}";
        }
    }
}
