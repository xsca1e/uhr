using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Android;
using BLE = BluetoothLEHardwareInterface;
using Device = IHeartRateManager.Device;
using Candidate = IHeartRateManager.Candidate;
using Packet = IHeartRateManager.Packet;
using ISerialize = IHeartRateManager.ISerialize;

public class HeartRateManagerMobile : IHeartRateManager
{
    public event Action<Candidate> OnFound;
    public event Action<Device> OnConnected;
    public event Action<Device> OnDisconnected;
    public event Action<Device, Packet> OnReceived;
    public event Action<string> OnError;

    private bool inited = false;
    private List<Candidate> candidates = new List<Candidate>();
    private Dictionary<string, Device> devices = new Dictionary<string, Device>();

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

    public void StartScan(string prefix = IHeartRateManager.PrefixIgnoreCase)
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

    public IEnumerator ScanTimeout(float timeout, string prefix = IHeartRateManager.PrefixIgnoreCase)
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

            if (characteristicUUID.ToLower() == IHeartRateManager.CommandUUID)
            {
                Debug.LogFormat("[HRM] BLE connecting, command UUID {0} found at service {1}", IHeartRateManager.CommandUUID, serviceUUID);
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
