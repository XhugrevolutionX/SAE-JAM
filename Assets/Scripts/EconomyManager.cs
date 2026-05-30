using UnityEngine;
using System;

public class EconomyManager : MonoBehaviour
{
    public static EconomyManager Instance { get; private set; }

    public int currentMoney { get; private set; }
    
    public event Action<int> OnMoneyChanged; 

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public void AddMoney(int amount)
    {
        currentMoney += amount;
        OnMoneyChanged?.Invoke(currentMoney);
        Debug.Log($"+{amount} money ! Total : {currentMoney}");
    }

    public bool TrySpend(int amount)
    {
        if (currentMoney >= amount)
        {
            currentMoney -= amount;
            OnMoneyChanged?.Invoke(currentMoney);
            Debug.Log($"you bought. remaining : {currentMoney}");
            return true;
        }
        
        Debug.Log("not enough money !");
        return false;
    }
}