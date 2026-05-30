using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

[Serializable]
public class ListWrapper
{
    public List<PaintableObject> objects = new List<PaintableObject>();
}

public class GameManager : MonoBehaviour
{
    [SerializeField] public List<ListWrapper> AllObjects;
    public GameObject[] RoomWall;

    public int CurrentRom = 0;
    
    private int _completed = 0;

    void Start()
    {
        foreach (var obj in AllObjects[CurrentRom].objects)
            obj.OnComplete += HandleComplete;
    }

    void HandleComplete(PaintableObject obj)
    {
        _completed++;
        Debug.Log($"{_completed}/{AllObjects[CurrentRom].objects.Count} objects done in Room {CurrentRom}");

        if (_completed >= AllObjects[CurrentRom].objects.Count)
        {
            // 1. Turn off the current wall (This works for Wall 0, Wall 1, etc.)
            if (CurrentRom < RoomWall.Length && RoomWall[CurrentRom] != null)
            {
                RoomWall[CurrentRom].SetActive(false);
            }

            // 2. CLEANUP: Unsubscribe from completed room objects so they don't break the next room
            foreach (var oldObj in AllObjects[CurrentRom].objects)
                oldObj.OnComplete -= HandleComplete;

            // 3. Move to the next room index
            CurrentRom++;
            _completed = 0; // Reset progress tracker for the next room

            // 4. FIX: Check if we have physically run out of rooms
            if (CurrentRom >= AllObjects.Count)
            {
                Debug.Log("CONGRATS! All rooms are fully cleared!");
                // Trigger your actual end-game UI / Win screen here
            }
            else
            {
                // 5. Initialize the next room's objects safely
                foreach (var PO in AllObjects[CurrentRom].objects)
                    PO.OnComplete += HandleComplete;
            
                Debug.Log($"Moving to Room {CurrentRom}. Go paint!");
            }
        }
    }
}