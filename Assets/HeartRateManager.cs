using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using BLE = BluetoothLEHardwareInterface;

public class HeartRateManager
{
    private static HeartRateManager instance = null;
    private static readonly object padlock = new object();

    // public const string PrefixIgnoreCase = "iChoiceR";
    public const string PrefixIgnoreCase = "if";

    // The datasheet includes 1 writing and 8 notifying characteristics.
    public const string CommandUUID = "d44bc439-abfd-45a2-b575-925416129600";
    public const string NotifyUUIDStart = "d44bc439-abfd-45a2-b575-925416129601";
    public const string NotifyUUIDEnd = "d44bc439-abfd-45a2-b575-925416129607";

    public interface ISerialize
    {
        public byte[] Serialize();
    }

    public interface IDeserialize
    {
        public void Deserialize(byte[] bytes);
    }

    public enum Code
    {
        Auth = 0xB1,
        Data = 0xD0,
        MAC = 0xBA,
        Display = 0xBB,
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

    public struct Data : IDeserialize
    {
        public byte Code;
        public uint IR1;
        public uint IR2;
        public byte Status;
        public byte SPO2;
        public ushort Interval;
        public byte Battery;
        public byte PI;
        public byte PR;
        public byte HRV;
        public byte SampleIndex;

        public void Deserialize(byte[] bytes)
        {
            using (var s = new MemoryStream(bytes))
            using (var r = new BinaryReader(s))
            {
                Code = r.ReadByte();
                // little endian
                IR1 = (uint)(r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16);
                IR2 = (uint)(r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16);
                Status = r.ReadByte();
                SPO2 = r.ReadByte();
                Interval = (ushort)(r.ReadByte() | r.ReadByte() << 8);
                Battery = r.ReadByte();
                PI = r.ReadByte();
                PR = r.ReadByte();
                HRV = r.ReadByte();
                SampleIndex = r.ReadByte();
            }
        }
    }

    public struct Packet : ISerialize, IDeserialize
    {
        public ushort Magic;
        public byte[] Data;

        public static ushort HOST_MAGIC = 0xaa55;
        public static ushort GUEST_MAGIC = 0x55aa;

        public byte[] Serialize()
        {
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                // Magic (big endian)
                w.Write((byte)(Magic >> 8));
                w.Write((byte)(Magic & 0xff));
                // Length
                w.Write((byte)(Data.Length + 1));
                // Data
                w.Write(Data);
                // Checksum
                w.Write((byte)(Data.Length + 1 + Data.Sum(b => b)));
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
                Data = r.ReadBytes(length - 1);
                byte _checksum = r.ReadByte();
            }
        }

        public bool Host()
        {
            return Magic == Packet.HOST_MAGIC;
        }

        public bool Guest()
        {
            return Magic == Packet.GUEST_MAGIC;
        }
    }

    public class Candidate
    {
        public string Name { get; }
        public string Address { get; }
        public Candidate(string name, string address) { Name = name; Address = address; }
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
            var device = new Device();
            device.Name = candidate.Name;
            device.Address = candidate.Address;
            device.ServiceUUID = serviceUUID;
            device.CommandCharacteristic = CommandUUID;
            List<string> uuids = new List<string>();
            int startDigit = int.Parse(NotifyUUIDStart.Substring(NotifyUUIDStart.Length - 1));
            int endDigit = int.Parse(NotifyUUIDEnd.Substring(NotifyUUIDEnd.Length - 1));
            for (int i = startDigit; i <= endDigit; i++)
            {
                string uuid = NotifyUUIDStart.Substring(0, NotifyUUIDStart.Length - 1) + i.ToString();
                uuids.Add(uuid);
            }
            device.NotifyCharacteristics = uuids.ToArray();
            return device;
        }
    }

    private List<Candidate> candidates;
    private Dictionary<string, Device> devices;

    public event Action<Candidate> OnFound;
    public event Action<Device> OnConnected;
    public event Action<Device> OnDisconnected;
    public event Action<Device, Data> OnReceived;
    public event Action<string> OnError;

    HeartRateManager() { }
    public static HeartRateManager Instance
    {
        get
        {
            lock (padlock)
            {
                if (instance == null)
                {
                    instance = new HeartRateManager();
                }
                return instance;
            }
        }
    }

    public static void Disable()
    {
        BLE.BluetoothEnable(false);
    }

    public static void Enable()
    {
        BLE.BluetoothEnable(true);
    }

    public void Init()
    {
        BLE.Initialize(true, false, () =>
        {
            Debug.Log("[HRM] BLE inited");
        }, (error) =>
        {
            Debug.LogFormat("[HRM] BLE error {0}", error);
            OnError?.Invoke(error);
        });
    }

    public void StartScan()
    {
        Debug.Log("[HRM] BLE start scan");
        candidates.Clear();
        BLE.ScanForPeripheralsWithServices(null, (address, name) =>
        {
            Debug.LogFormat("[HRM] BLE scan device {0} {1}", address, name);
            if (name.ToLower().StartsWith(PrefixIgnoreCase.ToLower()))
            {
                Debug.LogFormat("[HRM] BLE scan candidate {0} {1}", address, name);
                var candidate = new Candidate(name, address);
                OnFound?.Invoke(candidate);
                candidates.Add(candidate);
            }
        });
    }

    public void StopScan()
    {
        Debug.Log("[HRM] BLE stop scan");
        BLE.StopScan();
    }

    public IEnumerator ScanTimeout(float timeout)
    {
        Debug.LogFormat("[HRM] BLE scan for {0}s", timeout);
        StartScan();
        yield return new WaitForSeconds(timeout);
        StopScan();
        yield return null;
    }

    public List<Candidate> Candidates()
    {
        return candidates;
    }

    public List<Device> ConnectedDevices()
    {
        return devices.Values.ToList();
    }

    public void Send(Device device, ISerialize payload)
    {
        Packet packet;
        packet.Magic = Packet.HOST_MAGIC;
        packet.Data = payload.Serialize();
        var data = packet.Serialize();
        Debug.LogFormat("[HRM] Writing value {0} at {1} from service {2}", string.Join(',', data.Select(b => b.ToString("X2"))), device.CommandCharacteristic, device.ServiceUUID);
        BLE.WriteCharacteristic(device.Address, device.ServiceUUID, device.CommandCharacteristic, data, data.Length, true, (uuid) =>
        {
        });
    }

    public void RecvNotify(Device device, byte[] data)
    {
        var packet = new Packet();
        packet.Deserialize(data);
        if (packet.Data.Length != 0) {
            Code code = (Code)(packet.Data[0]);
            switch (code)
            {
                case Code.Data:
                    var payload = new Data();
                    payload.Deserialize(packet.Data);
                    OnReceived?.Invoke(device, payload);
                    break;
                default:
                    break;
            }
        }
    }

    private void SetupMTU(Device device)
    {
        BLE.RequestMtu(device.Address, 40, (_, mtu) =>
        {
            Debug.LogFormat("[HRM] BLE connecting, set MTU to {0}", mtu);
        });
    }

    private void SetupSubscriptions(Device device)
    {
        foreach (var notifyUUID in device.NotifyCharacteristics)
        {
            Debug.LogFormat("[HRM] Subscribing characteristic {0} from service {1} of device {2}", notifyUUID, device.ServiceUUID, device.Address);
            BLE.SubscribeCharacteristicWithDeviceAddress(device.Address, device.ServiceUUID, notifyUUID, (_, _) =>
            {
                Debug.LogFormat("[HRM] Received notification action without data at {0} of service {1}, ignoring", notifyUUID, device.ServiceUUID);
                /*
                BLE.ReadCharacteristic(device.Address, device.ServiceUUID, notifyUUID, (_, bytes) =>
                {
                    RecvNotify(device, bytes);
                });
                */
            }, (_, _, bytes) =>
            {
                Debug.LogFormat("[HRM] Received notification data at {0} of service {1}", notifyUUID, device.ServiceUUID);
                RecvNotify(device, bytes);
            });
        }
    }

    public void Connect(Candidate candidate)
    {
        BLE.ConnectToPeripheral(candidate.Address, null, null, (_, serviceUUID, characteristicUUID) =>
        {
            Debug.LogFormat("[HRM] BLE connecting, discover device {0} service {1} with characteristic {2}", candidate.Address, characteristicUUID, serviceUUID);
            if (characteristicUUID.ToLower() == CommandUUID)
            {
                Debug.LogFormat("[HRM] BLE connecting, command UUID {0} found at service {1}", CommandUUID, serviceUUID);
                var device = Device.FromCandidate(candidate, serviceUUID);
                // SetupMTU(device);
                SetupSubscriptions(device);
                OnConnected?.Invoke(device);
            }
        });
    }

    public void Disconnect(Device device)
    {
        BLE.DisconnectPeripheral(device.Address, (_) =>
        {
            Debug.LogFormat("[HRM] Disconnected {0}", device.Address);
            devices.Remove(device.Address);
            OnDisconnected?.Invoke(device);
        });
    }
}
