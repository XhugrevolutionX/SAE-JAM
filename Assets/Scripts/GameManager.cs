using UnityEngine;

public class GameManager : MonoBehaviour
{
    public PaintableObject[] objects;

    private int _completed = 0;

    void Start()
    {
        foreach (var obj in objects)
            obj.OnComplete += HandleComplete;
    }

    void HandleComplete(PaintableObject obj)
    {
        _completed++;
        Debug.Log($"{_completed}/{objects.Length} objects done");

        if (_completed >= objects.Length)
            Debug.Log("All done — trigger win screen here!");
    }
}