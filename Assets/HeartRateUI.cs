using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Device = HeartRateManager.Device;
using Candidate = HeartRateManager.Candidate;
using Check = HeartRateManager.Check;

public class HeartRateUI : MonoBehaviour
{
    public Button scanButton;
    public Button connectButton;

    [SerializeField]
    private TMP_Text deviceText;
    [SerializeField]
    private TMP_Text dataText;
    [SerializeField]
    private TMP_Text debugText;

    private Candidate candidate = null;
    private Device device = null;

    // Start is called before the first frame update
    void Start()
    {
        scanButton.GetComponent<Button>().onClick.AddListener(OnScanClick);
        connectButton.GetComponent<Button>().onClick.AddListener(OnConnectClick);
        HeartRateManager.Enable();
    }

    void OnScanClick()
    {
        StartCoroutine(ScanBLE());
    }

    void OnConnectClick()
    {
        if (candidate != null)
        {
            HeartRateManager.Instance.OnConnected += (d) =>
            {
                device = d;
                SetDeviceText(string.Format("{0} {1}", device.Address, device.Name));
                StartCoroutine(PairBLE());
            };
            HeartRateManager.Instance.Connect(candidate);
        } else
        {
            SetDebugText("Please scan first!");
        }
    }

    void SetDeviceText(string text)
    {
        deviceText.text = string.Format("Device: {0}", text);
    }

    void SetDataText(string text)
    {
        dataText.text = string.Format("Data: {0}", text);
    }

    void SetDebugText(string text)
    {
        debugText.text = string.Format("Debug: {0}", text);
    }

    IEnumerator PairBLE()
    {
        yield return new WaitForSeconds(2);
        HeartRateManager.Instance.Send(device, Check.Default());
    }

    IEnumerator ScanBLE()
    {
        HeartRateManager.Instance.Init();
        yield return new WaitForSeconds(2);
        HeartRateManager.Instance.OnFound += (c) =>
        {
            candidate = c;
            SetDebugText(string.Format("Candidate {0} {1}", c.Address, c.Name));
        };
        HeartRateManager.Instance.OnReceived += (device, data) =>
        {
            SetDataText(
                string.Format(
                    "IR1: {0} | IR2: {1} | SPO2: {2}% \nPR: {3}BPM | PI: {4:0.0}% | BAT: {5}%",
                    data.IR1, data.IR2, data.SPO2,
                    data.PR, ((float)(data.PI) / 10.0f), data.Battery
                )
            );
        };
        yield return HeartRateManager.Instance.ScanTimeout(5);
    }

    void Update()
    {
    }
}
