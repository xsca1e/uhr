
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using BLE = BluetoothLEHardwareInterface;
using Device = IHeartRateManager.Device;
using Candidate = IHeartRateManager.Candidate;
using Packet = IHeartRateManager.Packet;
using ISerialize = IHeartRateManager.ISerialize;
using System.Collections.Concurrent;

public class HeartRateManagerWindows : IHeartRateManager
{
    private enum ScanStatus { PROCESSING, AVAILABLE, FINISHED };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceUpdate
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string id;
        [MarshalAs(UnmanagedType.I1)]
        public bool isConnectable;
        [MarshalAs(UnmanagedType.I1)]
        public bool isConnectableUpdated;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
        public string name;
        [MarshalAs(UnmanagedType.I1)]
        public bool nameUpdated;
    }

    [DllImport("BleWinrtDll", EntryPoint = "StartDeviceScan")]
    private static extern void StartDeviceScan();

    [DllImport("BleWinrtDll", EntryPoint = "PollDevice")]
    private static extern ScanStatus PollDevice(out DeviceUpdate device, bool block);

    [DllImport("BleWinrtDll", EntryPoint = "StopDeviceScan")]
    private static extern void StopDeviceScan();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Service
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string uuid;
    };

    [DllImport("BleWinrtDll", EntryPoint = "ScanServices", CharSet = CharSet.Unicode)]
    private static extern void ScanServices(string deviceId);

    [DllImport("BleWinrtDll", EntryPoint = "PollService")]
    private static extern ScanStatus PollService(out Service service, bool block);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Characteristic
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string uuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string userDescription;
    };

    [DllImport("BleWinrtDll", EntryPoint = "ScanCharacteristics", CharSet = CharSet.Unicode)]
    private static extern void ScanCharacteristics(string deviceId, string serviceId);

    [DllImport("BleWinrtDll", EntryPoint = "PollCharacteristic")]
    private static extern ScanStatus PollCharacteristic(out Characteristic characteristic, bool block);

    [DllImport("BleWinrtDll", EntryPoint = "SubscribeCharacteristic", CharSet = CharSet.Unicode)]
    private static extern bool SubscribeCharacteristic(string deviceId, string serviceId, string characteristicId, bool block);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLEData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] buf;
        [MarshalAs(UnmanagedType.I2)]
        public short size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string deviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string serviceUuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string characteristicUuid;
    };

    [DllImport("BleWinrtDll", EntryPoint = "PollData")]
    private static extern bool PollData(out BLEData data, bool block);

    [DllImport("BleWinrtDll", EntryPoint = "SendData")]
    private static extern bool SendData(in BLEData data, bool block);

    [DllImport("BleWinrtDll", EntryPoint = "Quit")]
    private static extern void Quit();

    // Disconnect a single device
    // https://github.com/adabru/BleWinrtDll/issues/70#issuecomment-2857922743
    [DllImport("BleWinrtDll", EntryPoint = "Quit")]
    private static extern void Quit(string id);


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ErrorMessage
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string msg;
    };

    [DllImport("BleWinrtDll", EntryPoint = "GetError")]
    private static extern void GetError(out ErrorMessage buf);

    public event Action<Candidate> OnFound;
    public event Action<Device> OnConnected;
    public event Action<Device> OnDisconnected;
    public event Action<Device, Packet> OnReceived;
    public event Action<string> OnError;

    private enum State { IDLE, DEVICES, SERVICES, CHARACTERISTICS, RECV }
    private State state = State.IDLE;
    private Thread loop;
    private volatile bool running = true;
    private string currentPrefix = IHeartRateManager.PrefixIgnoreCase;

    private Queue<Action> commandQueue = new Queue<Action>();
    private Queue<Action> eventQueue = new Queue<Action>();
    private readonly object commandLock = new object();
    private readonly object eventLock = new object();

    private Candidate connectingCandidate;
    private ConcurrentDictionary<string, Candidate> candidates= new ConcurrentDictionary<string, Candidate>();
    private List<Service> discoveredServices = new List<Service>();
    private int currentServiceIndex;
    private List<Characteristic> currentServiceCharacteristics = new List<Characteristic>();
    private Device connectedDevice;

    private TimeSpan timeout = TimeSpan.FromSeconds(5);
    private DateTime lastReceived;

    public void Init()
    {
        Debug.LogFormat("[HRMW] WinRT API inited");
        OnError += (e) =>
        {
            Debug.LogFormat("[HRMW] Error {0}", e);
        };

        // Sometimes Windows keeps handles even after process exits, so here we clear them...
        Quit();
    }

    public void StartScan(string prefix = IHeartRateManager.PrefixIgnoreCase)
    {
        lock (commandLock)
        {
            Debug.LogFormat("[HRMW] Start scanning prefix \"{0}\"", prefix);
            commandQueue.Enqueue(() =>
            {
                currentPrefix = prefix;
                if (state == State.DEVICES)
                {
                    StopDeviceScan();
                }
                StartDeviceScan();
                state = State.DEVICES;
            });
        }
    }

    public void StopScan()
    {
        lock (commandLock)
        {
            Debug.LogFormat("[HRMW] Stop scan");
            commandQueue.Enqueue(() =>
            {
                if (state == State.DEVICES)
                {
                    StopDeviceScan();
                    state = State.IDLE;
                }
            });
        }
    }

    public void Connect(Candidate candidate)
    {
        lock (commandLock)
        {
            Debug.LogFormat("[HRMW] Connecting to candidate {0}", candidate.Display());
            commandQueue.Enqueue(() =>
            {
                if (state != State.IDLE) return;

                connectingCandidate = candidate;
                discoveredServices.Clear();
                currentServiceIndex = -1;
                currentServiceCharacteristics.Clear();

                ScanServices(candidate.Address);
                state = State.SERVICES;
            });
        }
    }

    public void Send(Device device, ISerialize packet)
    {
        lock (commandLock)
        {
            commandQueue.Enqueue(() =>
            {
                if (state != State.RECV || connectedDevice == null) return;

                BLEData data = new BLEData
                {
                    deviceId = device.Address,
                    serviceUuid = device.ServiceUUID,
                    characteristicUuid = device.CommandCharacteristic,
                    buf = new byte[512],
                    size = 0
                };

                byte[] bytes = packet.Serialize();
                if (bytes.Length > 512)
                {
                    EnqueueEvent(() => OnError?.Invoke("data size wrong"));
                    return;
                }

                Array.Copy(bytes, data.buf, bytes.Length);
                data.size = (short)bytes.Length;

                Debug.LogFormat("[HRMW] BLEData dump: {0}", JsonUtility.ToJson(data));
                Debug.LogFormat("[HRMW] Writing value {0} at {1} from service {2}", string.Join(',', bytes.Select(b => b.ToString("X2"))), device.CommandCharacteristic, device.ServiceUUID);
                if (!SendData(data, true))
                {
                    EnqueueEvent(() => OnError?.Invoke("send failed: " + GetLastError()));
                }
            });
        }
    }

    public void Disconnect(Device device)
    {
        lock (commandLock)
        {
            Debug.LogFormat("[HRMW] Disconnecting {0} ({1})", device.Name, device.Address);
            commandQueue.Enqueue(() =>
            {
                if (state == State.RECV && connectedDevice != null)
                {
                    var disconnectedDevice = connectedDevice;
                    connectedDevice = null;
                    state = State.IDLE;
                    Quit(device.Address);
                    EnqueueEvent(() => OnDisconnected?.Invoke(disconnectedDevice));
                }
            });
        }
    }

    /*
    public IEnumerator Run()
    {
        while (running)
        {
            switch (state)
            {
                case State.IDLE:
                    Debug.Log("[HRMW FSM] IDLE");
                    yield return new WaitForSeconds(0.5f);
                    break;
                case State.SCAN:
                    yield return PollScan();
                    break;
                case State.CONNECT:
                    while (devices.IsEmpty)
                    {
                        yield return new WaitForSeconds(0.5f);
                    }
                    foreach (var device in devices.Values)
                    {
                        OnConnected?.Invoke(device);
                    }
                    SetState(State.RECV);
                    break;
                case State.RECV:
                    yield return PollRecv();
                    break;
            }
        }
    }
    */

    private void Loop()
    {
        while (running)
        {
            lock (commandLock)
            {
                while (commandQueue.Count > 0)
                {
                    commandQueue.Dequeue()?.Invoke();
                }
            }

            switch (state)
            {
                case State.IDLE:
                    Debug.Log("[HRMW FSM] IDLE");
                    Thread.Sleep(500);
                    break;
                case State.DEVICES:
                    HandleDeviceScanning();
                    break;
                case State.SERVICES:
                    HandleServiceScanning();
                    break;
                case State.CHARACTERISTICS:
                    HandleCharacteristicScanning();
                    break;
                case State.RECV:
                    HandleDataPolling();
                    break;
            }

            Thread.Sleep(50);
        }

        Quit();
    }

    public IEnumerator Run()
    {
        loop = new Thread(Loop);
        loop.Start();

        while (running)
        {
            lock (eventLock)
            {
                while (eventQueue.Count > 0)
                {
                    eventQueue.Dequeue()?.Invoke();
                }
            }

            yield return null;
        }

        loop.Join();
    }

    /*

    public IEnumerator RunDebug()
    {
        yield return Run();
    }
    */

    private void EnqueueEvent(Action action)
    {
        lock (eventLock)
        {
            eventQueue.Enqueue(action);
        }
    }

    private string GetLastError()
    {
        ErrorMessage err;
        GetError(out err);
        return err.msg;
    }

    private void HandleDeviceScanning()
    {
        DeviceUpdate deviceUpdate;
        ScanStatus status = PollDevice(out deviceUpdate, false);

        if (status == ScanStatus.AVAILABLE)
        {
            var candidate = new Candidate(deviceUpdate.name, deviceUpdate.id);
            Debug.LogFormat("[HRMW] Scan polled device {0}, connectable: {1}", candidate.Display(), deviceUpdate.isConnectable); ;
            if (candidate.Name != null &&
                candidate.Name.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidates[candidate.Address] = candidate;
                Debug.LogFormat("[HRMW] Scan polled candidate {0}", candidate.Display());
            }

            if (candidates.TryGetValue(deviceUpdate.id, out candidate) && deviceUpdate.isConnectable)
            {
                Debug.LogFormat("[HRMW] Scan candidate connectable {0}", candidate.Display());
                EnqueueEvent(() => OnFound?.Invoke(candidate));
            }
        }
        else if (status == ScanStatus.FINISHED)
        {
            // Seems like windows return FINISHED after few minutes, to keep the api
            // consistent here we start the scan again.
            Debug.Log("[HRMW] Scan hits windows timeout, restart");
            StartDeviceScan();
        }
    }

    private void HandleServiceScanning()
    {
        Service service;
        // ScanStatus status = PollService(out service, true);
        ScanStatus status = PollService(out service, false);

        if (status == ScanStatus.AVAILABLE)
        {
            Debug.LogFormat("[HRMW] Polled service {0}", service.uuid);
            discoveredServices.Add(service);
        }
        else if (status == ScanStatus.FINISHED)
        {
            if (discoveredServices.Count == 0)
            {
                EnqueueEvent(() => OnError?.Invoke("no services"));
                state = State.SERVICES;
                ScanServices(connectingCandidate.Address);
                return;
            }

            currentServiceIndex = 0;
            ScanCharacteristics(connectingCandidate.Address, discoveredServices[currentServiceIndex].uuid);
            state = State.CHARACTERISTICS;
        }
    }

    private void HandleCharacteristicScanning()
    {
        Characteristic characteristic;
        // ScanStatus status = PollCharacteristic(out characteristic, true);
        ScanStatus status = PollCharacteristic(out characteristic, false);
        var service = discoveredServices[currentServiceIndex];

        if (status == ScanStatus.AVAILABLE)
        {
            Debug.LogFormat("[HRMW] Polled characteristics {0} from service {1}", characteristic.uuid, service.uuid);
            currentServiceCharacteristics.Add(characteristic);
        }
        else if (status == ScanStatus.FINISHED)
        {
            string commandUuid = IHeartRateManager.CommandUUID.ToLower();

            bool foundCommand = false;
            List<string> foundNotifyChars = new List<string>();

            foreach (var c in currentServiceCharacteristics)
            {
                string uuid = c.uuid.ToLower().Replace("{", "").Replace("}", "");

                if (uuid == commandUuid)
                {
                    foundCommand = true;
                }
            }

            if (foundCommand)
            {
                var device = Device.FromCandidate(connectingCandidate, service.uuid);

                foreach (var uuid in device.NotifyCharacteristics)
                {
                    Debug.LogFormat("[HRMW] Subscribing characteristic {0} from service {1}", uuid, service.uuid);
                    if (!SubscribeCharacteristic(connectingCandidate.Address,
                        service.uuid, uuid, false))
                    {
                        EnqueueEvent(() => OnError?.Invoke(
                            $"subscribe error: {GetLastError()}"));
                    }
                }

                connectedDevice = device;
                state = State.RECV;
                lastReceived = DateTime.Now;
                EnqueueEvent(() => OnConnected?.Invoke(device));
            }
            else
            {
                currentServiceIndex++;
                if (currentServiceIndex < discoveredServices.Count)
                {
                    currentServiceCharacteristics.Clear();
                    ScanCharacteristics(connectingCandidate.Address,
                        discoveredServices[currentServiceIndex].uuid);
                }
                else
                {
                    EnqueueEvent(() => OnError?.Invoke("command UUID not found"));
                    state = State.IDLE;
                }
            }
        }
    }

    private void HandleDataPolling()
    {
        BLEData bleData;
        if (PollData(out bleData, false))
        {
            Debug.LogFormat("[HRMW] Received notification data at {0} of service {1}", bleData.characteristicUuid, bleData.serviceUuid);
            byte[] bytes = new byte[bleData.size];
            Array.Copy(bleData.buf, bytes, bleData.size);

            Packet packet = new Packet();
            packet.Deserialize(bytes);

            lastReceived = DateTime.Now;
            EnqueueEvent(() => OnReceived?.Invoke(connectedDevice, packet));
        } else
        {
            var elapsed = DateTime.Now - lastReceived;
            if (elapsed > timeout)
            {
                Debug.LogFormat("[HRMW] Last recv timeout elapsed for {0}, disconnect", elapsed);
                Disconnect(connectedDevice);
            }
        }
    }
}