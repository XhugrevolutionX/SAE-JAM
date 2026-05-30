using UnityEngine;
using TMPro;

public class MoneyUI : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI moneyText;

    void Start()
    {
        UpdateUI(EconomyManager.Instance.currentMoney);

        EconomyManager.Instance.OnMoneyChanged += UpdateUI;
    }

    void OnDestroy()
    {
        if (EconomyManager.Instance != null)
        {
            EconomyManager.Instance.OnMoneyChanged -= UpdateUI;
        }
    }

    private void UpdateUI(int currentMoney)
    {
        moneyText.text = currentMoney + " Money";
    }
}