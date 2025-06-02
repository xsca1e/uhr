using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Device = IHeartRateManager.Device;
using Candidate = IHeartRateManager.Candidate;
using Packet = IHeartRateManager.Packet;
using Reading = IHeartRateManager.Reading;
using DisplayMode = IHeartRateManager.DisplayMode;
using PayloadType = IHeartRateManager.PayloadType;

public class HeartRateUIController : MonoBehaviour
{
    [SerializeField]
    private UIDocument uiDocument;

    private VisualElement root;
    private VisualElement scanView;
    private VisualElement dataView;
    private ListView deviceList;
    private Label scanStatus;
    private Label errorLabel;

    private Label deviceName;
    private Label macInfo;

    private Label prValue;
    private Label spo2Value;
    private Label piValue;
    private Label rrValue;
    private Label ir1Value;
    private Label ir2Value;
    private Label sdnnValue;
    private Label sampleValue;
    private Label batteryValue;

    private Button scanButton;
    private Toggle autoConnectToggle;
    private Button disconnectButton;
    private Button[] modeButtons;

    private IHeartRateManager hrm;
    private List<Candidate> candidates = new List<Candidate>();
    private Device connectedDevice;
    private string autoConnectAddress;
    private bool shouldAutoConnect = true;

    private void Start()
    {
        root = uiDocument.rootVisualElement;
        scanView = root.Q<VisualElement>("ScanView");
        dataView = root.Q<VisualElement>("DataView");
        deviceList = root.Q<ListView>("DeviceList");
        scanStatus = root.Q<Label>("ScanStatus");
        errorLabel = root.Q<Label>("ErrorLabel");
        scanButton = root.Q<Button>("ScanButton");

        deviceName = root.Q<Label>("deviceName");
        macInfo = root.Q<Label>("macInfo");

        prValue = root.Q<Label>("prValue");
        spo2Value = root.Q<Label>("spo2Value");
        piValue = root.Q<Label>("piValue");
        rrValue = root.Q<Label>("rrValue");
        ir1Value = root.Q<Label>("ir1Value");
        ir2Value = root.Q<Label>("ir2Value");
        sdnnValue = root.Q<Label>("sdnnValue");
        sampleValue = root.Q<Label>("sampleValue");
        batteryValue = root.Q<Label>("batteryValue");

        autoConnectToggle = root.Q<Toggle>("AutoConnectToggle");
        disconnectButton = root.Q<Button>("DisconnectButton");
        modeButtons = root.Q<VisualElement>("modes").Children().Select(v => v as Button).ToArray();

        var i = 0;
        foreach (var modeButton in modeButtons)
        {
            modeButton.clicked += () =>
            {
                OnModeButtonClicked(modeButton);
            };
            i++;
        }

        deviceList.makeItem = () => new Label();
        deviceList.bindItem = (element, index) =>
            ((Label)element).text = candidates[index].Display();
        deviceList.itemsSource = candidates;
        deviceList.onSelectionChange += OnDeviceSelected;

        scanButton.clicked += OnScanButtonClicked;
        disconnectButton.clicked += OnDisconnect;
        autoConnectToggle.RegisterValueChangedCallback(OnAutoConnectChanged);

        hrm = HeartRateManager.Instance;
        hrm.OnFound += OnDeviceFound;
        hrm.OnConnected += OnDeviceConnected;
        hrm.OnDisconnected += OnDeviceDisconnected;
        hrm.OnReceived += OnPacketReceived;

        autoConnectAddress = PlayerPrefs.GetString("AutoConnectAddress", "");
        Debug.LogFormat("[DBG] Auto connect address: {0}", autoConnectAddress);

        // HeartRateManager.CheckPermissions();
        hrm.Init();
        StartCoroutine(hrm.Run());
        StartScan();
        // StartCoroutine(hrm.RunDebug());
    }

    private void OnScanButtonClicked()
    {
        if (scanButton.enabledSelf)
        {
            if (scanButton.text == "STOP")
            {
                StopScan();

            }
            else
            {
                StartScan();
            }
        }
    }

    /*
    private IEnumerator Scan()
    {
        candidates.Clear();
        yield return hrm.ScanTimeout(4f);
        shouldAutoConnect = false;
        yield return null;
    }
    */

    private void StopScan()
    {
        Debug.Log("[DBG] UI stop scan");
        hrm.StopScan();
        scanStatus.text = "Scan stopped.";
        scanButton.text = "RESCAN";
        scanButton.EnableInClassList("stop-btn", false);
    }

    private IEnumerator DelayedStartScan()
    {
        yield return new WaitForSeconds(3);
        scanButton.text = "STOP";
        scanButton.EnableInClassList("stop-btn", true);
        scanButton.SetEnabled(true);
        hrm.StartScan();
        yield return null;
    }

    private void StartScan()
    {
        deviceList.SetEnabled(true);
        Debug.Log("[DBG] UI start scan");
        candidates.Clear();
        deviceList.Rebuild();
        scanStatus.text = "Scanning for devices...";
        scanButton.SetEnabled(false);
        StartCoroutine(DelayedStartScan());
    }

    private void OnDeviceFound(Candidate candidate)
    {
        if (!candidates.Exists(c => c.Address == candidate.Address))
        {
            candidates.Add(candidate);
            deviceList.Rebuild();
        }

        if (shouldAutoConnect && autoConnectAddress == candidate.Address)
        {
            Debug.LogFormat("[DBG] Found auto connect target {0}", candidate.Display());
            Connect(candidate);
        }
    }

    private void Connect(Candidate candidate)
    {
        StopScan();
        deviceList.SetEnabled(false);
        hrm.Connect(candidate);
        scanStatus.text = "Connecting...";
    }

    private void OnDeviceSelected(IEnumerable<object> selected)
    {
        foreach (var item in selected)
        {
            int index = candidates.IndexOf((Candidate)item);
            if (index >= 0)
            {
                Connect(candidates[index]);
            }
        }
    }

    private void OnDeviceConnected(Device device)
    {
        deviceName.text = device.Name;
        // Enable auto connect even user actively disconnects
        // shouldAutoConnect = false;
        shouldAutoConnect = true;
        connectedDevice = device;
        scanView.style.display = DisplayStyle.None;
        dataView.style.display = DisplayStyle.Flex;
        autoConnectToggle.value = device.Address == autoConnectAddress;
        StartCoroutine(Handshake(device));
    }

    private IEnumerator Handshake(Device device)
    {
        yield return new WaitForSeconds(1);
        hrm.Send(device, Packet.AuthCommand());
        yield return new WaitForSeconds(1);
        hrm.Send(device, Packet.MACCommand());
        yield return null;
    }

    private void OnPacketReceived(Device device, Packet packet)
    {
        switch (packet.PayloadType)
        {
            case PayloadType.Reading:
                var reading = packet.PayloadReading();
                prValue.text = reading.PR.ToString();
                spo2Value.text = reading.SPO2.ToString();
                piValue.text = $"{reading.PI / 10.0f}";
                rrValue.text = $"{reading.RRInterval * 2}";
                ir1Value.text = reading.IR1.ToString();
                ir2Value.text = reading.IR2.ToString();
                sdnnValue.text = reading.SDNN.ToString();
                sampleValue.text = reading.SampleIndex.ToString();
                batteryValue.text = reading.Battery.ToString();
                break;
            case PayloadType.MAC:
                var mac = packet.PayloadMAC();
                macInfo.text = string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));
                break;
            default:
                break;
        }
    }

    private void OnDisconnect()
    {
        if (connectedDevice != null)
        {
            hrm.Disconnect(connectedDevice);
        }
    }

    private void OnDeviceDisconnected(Device device)
    {
        prValue.text = spo2Value.text = piValue.text = rrValue.text = "-";
        ir1Value.text = ir2Value.text = "-";
        sdnnValue.text = sampleValue.text = batteryValue.text = "-";
        deviceName.text = macInfo.text = "-";
        connectedDevice = null;
        scanView.style.display = DisplayStyle.Flex;
        dataView.style.display = DisplayStyle.None;
        StartScan();
    }

    private void OnAutoConnectChanged(ChangeEvent<bool> evt)
    {
        if (evt.newValue && connectedDevice != null)
        {
            PlayerPrefs.SetString("AutoConnectAddress", connectedDevice.Address);
            autoConnectAddress = connectedDevice.Address;
        }
        else
        {
            PlayerPrefs.DeleteKey("AutoConnectAddress");
            autoConnectAddress = "";
        }
    }

    private void OnModeButtonClicked(Button button)
    {
        if (connectedDevice != null)
        {
            DisplayMode mode = DisplayMode.PR_SPO2;
            switch (button.name)
            {
                case "PR_SPO2":
                    mode = DisplayMode.PR_SPO2;
                    break;
                case "PR_RLX":
                    mode = DisplayMode.PR_RLX;
                    break;
                case "RMSSD_SPO2":
                    mode = DisplayMode.RMSSD_SPO2;
                    break;
                case "SDNN_SPO2":
                    mode = DisplayMode.SDNN_SPO2;
                    break;
            }
            Debug.LogFormat("[DBG] Switching to mode {0} (value {1:X})", button.name, (byte)mode);
            hrm.Send(connectedDevice, Packet.DisplayCommand(mode));
        }
    }

    private void OnDestroy()
    {
        hrm.StopScan();
        // HeartRateManager.Disable();
    }
}