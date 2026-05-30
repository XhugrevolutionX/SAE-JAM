using UnityEngine;

[RequireComponent(typeof(PaintableObject))]
public class PaintReward : MonoBehaviour
{
    [Header("Récompense")]
    public int rewardAmount = 50;

    private PaintableObject _paintable;

    void Awake()
    {
        _paintable = GetComponent<PaintableObject>();
    }

    void OnEnable()
    {
        _paintable.OnComplete += GiveReward;
    }

    void OnDisable()
    {
        _paintable.OnComplete -= GiveReward;
    }

    private void GiveReward(PaintableObject obj)
    {
        EconomyManager.Instance.AddMoney(rewardAmount);
        
        _paintable.OnComplete -= GiveReward; 
    }
}