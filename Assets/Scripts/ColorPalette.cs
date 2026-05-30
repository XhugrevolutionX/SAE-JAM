using UnityEngine;

[CreateAssetMenu(fileName = "ColorPalette", menuName = "Painting/Color Palette")]
public class ColorPalette : ScriptableObject
{
    public Color[] colors = new Color[]
    {
        Color.red,
        Color.blue,
        Color.green,
        Color.yellow,
    };
}
