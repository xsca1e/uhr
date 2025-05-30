using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.Collections.Generic;
using Device = HeartRateManager.Device;
using Candidate = HeartRateManager.Candidate;
using Data = HeartRateManager.Data;
using Check = HeartRateManager.Check;

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

    private HeartRateManager hrm;
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

        deviceList.makeItem = () => new Label();
        deviceList.bindItem = (element, index) =>
            ((Label)element).text = $"{candidates[index].Name} ({candidates[index].Address})";
        deviceList.itemsSource = candidates;
        deviceList.onSelectionChange += OnDeviceSelected;

        scanButton.clicked += OnScanButtonClicked;
        disconnectButton.clicked += OnDisconnect;
        autoConnectToggle.RegisterValueChangedCallback(OnAutoConnectChanged);

        hrm = HeartRateManager.Instance;
        hrm.OnFound += OnDeviceFound;
        hrm.OnConnected += OnDeviceConnected;
        hrm.OnDisconnected += OnDeviceDisconnected;
        hrm.OnReceived += OnDataReceived;

        autoConnectAddress = PlayerPrefs.GetString("AutoConnectAddress", "");
        Debug.LogFormat("[DBG] Auto connect address: {0}", autoConnectAddress);

        HeartRateManager.CheckPermissions();
        hrm.Init();

        StartScan();
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
            Debug.LogFormat("[DBG] Found auto connect target {0} ({1})", candidate.Name, candidate.Address);
            hrm.Connect(candidate);
        }
    }

    private void OnDeviceSelected(IEnumerable<object> selected)
    {
        foreach (var item in selected)
        {
            int index = candidates.IndexOf((Candidate)item);
            if (index >= 0)
            {
                StopScan();
                hrm.Connect(candidates[index]);
            }
        }
    }

    private void OnDeviceConnected(Device device)
    {
        shouldAutoConnect = false;
        connectedDevice = device;
        scanView.style.display = DisplayStyle.None;
        dataView.style.display = DisplayStyle.Flex;
        autoConnectToggle.value = device.Address == autoConnectAddress;
        StartCoroutine(Handshake(device));
    }

    private IEnumerator Handshake(Device device)
    {
        yield return new WaitForSeconds(2);
        hrm.Send(device, Check.Default());
        yield return null;
    }

    private void OnDataReceived(Device device, Data data)
    {
        prValue.text = data.PR.ToString();
        spo2Value.text = data.SPO2.ToString();
        piValue.text = $"{data.PI / 10.0f}";
        rrValue.text = $"{data.RRInterval * 2}";
        ir1Value.text = data.IR1.ToString();
        ir2Value.text = data.IR2.ToString();
        sdnnValue.text = data.SDNN.ToString();
        sampleValue.text = data.SampleIndex.ToString();
        batteryValue.text = data.Battery.ToString();
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

    private void OnDestroy()
    {
        hrm.StopScan();
        // HeartRateManager.Disable();
    }
}