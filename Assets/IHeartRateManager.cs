using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.IO;
using UnityEngine;

public interface IHeartRateManager
{
    event Action<Candidate> OnFound;
    event Action<Device> OnConnected;
    event Action<Device> OnDisconnected;
    event Action<Device, Packet> OnReceived;
    event Action<string> OnError;

    // public const string PrefixIgnoreCase = "iChoiceR";
    const string PrefixIgnoreCase = "if";

    // The datasheet includes 1 writing and 8 notifying characteristics.
    const string CommandUUID = "d44bc439-abfd-45a2-b575-925416129600";
    const string NotifyUUIDStart = "d44bc439-abfd-45a2-b575-925416129601";
    const string NotifyUUIDEnd = "d44bc439-abfd-45a2-b575-925416129607";

    interface ISerialize
    {
        public byte[] Serialize();
    }

    public interface IDeserialize
    {
        public void Deserialize(byte[] bytes);
    }

    public enum PayloadType
    {
        Auth = 0xb1,
        Reading = 0xd0,
        MAC = 0xba,
        Display = 0xbb,
        Unknown = 0xff,
    }

    public enum DisplayMode
    {
        PR_SPO2 = 0x01,
        PR_RLX = 0x02,
        RMSSD_SPO2 = 0x03,
        SDNN_SPO2 = 0x04,
    }

    public struct Check : ISerialize
    {
        public byte Request;
        public byte[] Password;

        public byte[] Serialize()
        {
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write(Request);
                w.Write(Password);
                return s.ToArray();
            }
        }

        public static Check Default()
        {
            return new Check(new byte[] { 0x00, 0x00 });
        }

        public Check(byte[] password)
        {
            Request = 0xb1;
            Password = password;
        }
    }

    public struct Reading : IDeserialize
    {
        public uint IR1;
        public uint IR2;
        public byte Status;
        public byte SPO2;
        public ushort RRInterval;
        public byte Battery;
        public byte PI;
        public byte PR;
        public byte SDNN;
        public byte SampleIndex;

        public void Deserialize(byte[] bytes)
        {
            using (var s = new MemoryStream(bytes))
            using (var r = new BinaryReader(s))
            {
                // little endian
                IR1 = (uint)(r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16);
                IR2 = (uint)(r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16);
                Status = r.ReadByte();
                SPO2 = r.ReadByte();
                RRInterval = (ushort)(r.ReadByte() | r.ReadByte() << 8);
                Battery = r.ReadByte();
                PI = r.ReadByte();
                PR = r.ReadByte();
                SDNN = r.ReadByte();
                SampleIndex = r.ReadByte();
            }
        }
    }

    public struct Packet : ISerialize, IDeserialize
    {
        public ushort Magic;
        public PayloadType PayloadType;
        public byte[] Payload;

        public static ushort HOST_MAGIC = 0xaa55;
        public static ushort GUEST_MAGIC = 0x55aa;

        public byte[] Serialize()
        {
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                int checksum = 0;
                // Magic (big endian)
                w.Write((byte)(Magic >> 8));
                w.Write((byte)(Magic & 0xff));
                // Length
                w.Write((byte)(Payload.Length + 2));
                checksum += (byte)(Payload.Length + 2);
                // Data Type
                w.Write((byte)PayloadType);
                checksum += (byte)PayloadType;
                // Data
                if (Payload != null)
                {
                    w.Write(Payload);
                    checksum += Payload.Sum(b => b);
                }
                // Checksum
                w.Write((byte)checksum);
                return s.ToArray();
            }
        }

        public void Deserialize(byte[] bytes)
        {
            using (var s = new MemoryStream(bytes))
            using (var r = new BinaryReader(s))
            {
                // Magic (big endian)
                Magic = (ushort)(r.ReadByte() << 8 + r.ReadByte());
                byte length = r.ReadByte();
                if (length >= 2)
                {
                    PayloadType = (PayloadType)r.ReadByte();
                    Payload = r.ReadBytes(length - 2);
                    byte _checksum = r.ReadByte();
                }
                else
                {
                    PayloadType = PayloadType.Unknown;
                }
            }
        }

        public bool Host()
        {
            return Magic == HOST_MAGIC;
        }

        public bool Guest()
        {
            return Magic == GUEST_MAGIC;
        }

        public PayloadType Type()
        {
            return PayloadType;
        }

        public static Packet Command(PayloadType type, ISerialize payload)
        {
            var packet = new Packet();
            packet.Magic = HOST_MAGIC;
            packet.Payload = payload.Serialize();
            return packet;
        }

        public void TestSerialize()
        {
            Debug.Assert(AuthCommand().Serialize() == new byte[]
            {
                0x55, 0xaa,
                0x03,
                0xb1,
                0x00, 0x00,
                0xb5
            });
        }

        /*
        public void TestDeserialize()
        {
            byte[] packet = { 0x55, 0xaa, 0x11, 0xd0, };
        }
        */

        public static Packet Command(PayloadType type)
        {
            var packet = new Packet();
            packet.Magic = HOST_MAGIC;
            packet.PayloadType = type;
            packet.Payload = null;
            return packet;
        }

        public static Packet Command(PayloadType type, byte[] payload)
        {
            var packet = new Packet();
            packet.Magic = HOST_MAGIC;
            packet.PayloadType = type;
            packet.Payload = payload;
            return packet;
        }

        public PhysicalAddress PayloadMAC()
        {
            return new PhysicalAddress(Payload);
        }

        public Reading PayloadReading()
        {
            var reading = new Reading();
            reading.Deserialize(Payload);
            return reading;
        }

        public static Packet AuthCommand()
        {
            return AuthCommand(new byte[] { 0x00, 0x00 });
        }

        public static Packet AuthCommand(byte[] password)
        {
            return Command(PayloadType.Auth, password);
        }

        public static Packet MACCommand()
        {
            return Command(PayloadType.MAC, new byte[] { });
        }

        public static Packet DisplayCommand(DisplayMode mode)
        {
            return Command(PayloadType.Display, new byte[] { (byte)mode });
        }
    }

    public class Candidate
    {
        public string Name { get; }
        public string Address { get; }
        public Candidate(string name, string address) { Name = name; Address = address; }
        public string DisplayName()
        {
            if (Name == null || Name.Trim().Length == 0)
            {
                return "Unknown";
            } else
            {
                return Name;
            }
        }
        public string DisplayAddress()
        {
            if (Address.Contains("#"))
            {
                return Address.Split("-").LastOrDefault();
            } else
            {
                return Address;
            }
        }
        public string Display()
        {
            return string.Format("{0} ({1})", DisplayName(), DisplayAddress());
        }
    }

    public class Device
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string ServiceUUID { get; set; } = null;
        public string CommandCharacteristic { get; set; } = null;
        public string[] NotifyCharacteristics { get; set; } = null;
        public static Device FromCandidate(Candidate candidate, string serviceUUID)
        {
            bool braced = serviceUUID.Contains("{");
            var device = new Device();
            device.Name = candidate.Name;
            device.Address = candidate.Address;
            device.ServiceUUID = serviceUUID;
            if (braced)
            {
                device.CommandCharacteristic = "{" + CommandUUID + "}";
            } else
            {
                device.CommandCharacteristic = CommandUUID;
            }
            List<string> uuids = new List<string>();
            int startDigit = int.Parse(NotifyUUIDStart.Substring(NotifyUUIDStart.Length - 1));
            int endDigit = int.Parse(NotifyUUIDEnd.Substring(NotifyUUIDEnd.Length - 1));
            for (int i = startDigit; i <= endDigit; i++)
            {
                string uuid = NotifyUUIDStart.Substring(0, NotifyUUIDStart.Length - 1) + i.ToString();
                uuids.Add(uuid);
            }
            if (braced)
            {
                device.NotifyCharacteristics = uuids.Select(uuid => "{" + uuid + "}").ToArray();
            }
            else
            {
                device.NotifyCharacteristics = uuids.ToArray();
            }
            return device;
        }
    }

    void Init();
    void StartScan(string prefix = PrefixIgnoreCase);
    void StopScan();
    void Connect(Candidate candidate);
    void Send(Device device, ISerialize packet);
    void Disconnect(Device device);
    IEnumerator Run() { yield return null; }
    IEnumerator RunDebug() { yield return null; }
}
