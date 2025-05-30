using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Candidate = HeartRateManager.Candidate;

public class DeviceButton : MonoBehaviour
{
    [SerializeField] private TMP_Text deviceNameText;
    [SerializeField] private TMP_Text deviceAddressText;
    [SerializeField] private Button button;
    [SerializeField] private Image background;

    private Color originalColor;

    private void Awake()
    {
        originalColor = background.color;
    }

    public void Initialize(Candidate candidate, System.Action onClick)
    {
        deviceNameText.text = candidate.Name;
        deviceAddressText.text = candidate.Address;
        button.onClick.AddListener(() => onClick?.Invoke());
    }
}