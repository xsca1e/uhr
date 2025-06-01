using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using UnityEngine;
using UnityEngine.Android;
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

    private bool inited = false;
    private List<Candidate> candidates;
    private Dictionary<string, Device> devices;

    public event Action<Candidate> OnFound;
    public event Action<Device> OnConnected;
    public event Action<Device> OnDisconnected;
    public event Action<Device, Packet> OnReceived;
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
                    instance.candidates = new List<Candidate>();
                    instance.devices = new Dictionary<string, Device>();
                }
                return instance;
            }
        }
    }

    public static void Disable()
    {
        Debug.LogFormat("[HRM] Disabling Bluetooth");
        BLE.BluetoothEnable(false);
    }

    private static void RequestPermissions(string[] permissions)
    {
        var requests = new List<string>();
        foreach (string permission in permissions)
        {
            if (!Permission.HasUserAuthorizedPermission(permission))
            {
                requests.Add(permission);
            }
        }
        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += (permission) =>
        {
            Debug.LogFormat("[HRM] Granted permission {0}", permission);
        };
        callbacks.PermissionDenied += (permission) =>
        {
            Debug.LogFormat("[HRM] Denied permission {0}", permission);
        };
        Permission.RequestUserPermissions(requests.ToArray(), callbacks);
    }

    public static void CheckPermissions()
    {
        Debug.Log("[HRM] Enabling Bluetooth");
#if UNITY_ANDROID
        if (AndroidVersion.SDK_INT >= 31)
        {
            Debug.Log("[HRM] Android SDK >= S");
            RequestPermissions(new string[]
            {
                Permission.FineLocation,
                Permission.CoarseLocation,
                "BLUETOOTH_CONNECT",
                "BLUETOOTH_SCAN"
            });
        }
        else
        {
            Debug.Log("[HRM] Android SDK < S");
            RequestPermissions(new string[]
                {
                Permission.FineLocation,
                Permission.CoarseLocation,
                }
            );
        }
#endif
    }

    public void Init()
    {
        BLE.Initialize(true, false, () =>
        {
            Debug.Log("[HRM] BLE inited");
            inited = true;
        }, (error) =>
        {
            Debug.LogFormat("[HRM] BLE error {0}", error);
            if (error.Contains("Not Enabled"))
            {
                BLE.BluetoothEnable(true);
            }
            else
            {
                OnError?.Invoke(error);
            }
        });
    }

    public bool HasInited()
    {
        return inited;
    }

    public void StartScan(string prefix = PrefixIgnoreCase)
    {
        candidates.Clear();
        Debug.LogFormat("[HRM] BLE start scan for prefix \"{0}\"", prefix);
        BLE.BluetoothScanMode(BLE.ScanMode.LowPower);
        BLE.ScanForPeripheralsWithServices(null, (address, name) =>
        {
            Debug.LogFormat("[HRM] BLE scan device {0} {1}", address, name);
            if (name.ToLower().StartsWith(prefix.ToLower()))
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

    public IEnumerator ScanTimeout(float timeout, string prefix = PrefixIgnoreCase)
    {
        Debug.LogFormat("[HRM] BLE scan for {0}s", timeout);
        StartScan(prefix);
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

    public void Send(Device device, ISerialize packet)
    {
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
        OnReceived?.Invoke(device, packet);
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
                devices.Add(device.Address, device);
                OnConnected?.Invoke(device);
            }
        }, (address) =>
        {
            Device device;
            if (devices.TryGetValue(address, out device))
            {
                Debug.LogFormat("[HRM] Lost connection {0}", device.Address);
                devices.Remove(device.Address);
                OnDisconnected?.Invoke(device);
            }
        });
    }

    public void Disconnect(Device device)
    {
        BLE.DisconnectPeripheral(device.Address, (_) =>
        {
            Debug.LogFormat("[HRM] Disconnected {0}", device.Address);
            if (devices.ContainsKey(device.Address))
            {
                devices.Remove(device.Address);
                OnDisconnected?.Invoke(device);
            }
        });
    }
}
