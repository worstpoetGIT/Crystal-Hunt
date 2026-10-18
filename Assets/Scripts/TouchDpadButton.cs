using UnityEngine;
using UnityEngine.EventSystems;

public class TouchDpadButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public enum Dir { Up, Down, Left, Right }
    public Dir direction;

    public void OnPointerDown(PointerEventData _) { Set(true); }
    public void OnPointerUp(PointerEventData _)   { Set(false); }
    public void OnPointerExit(PointerEventData _) { Set(false); }

    void Set(bool pressed)
    {
        if (!TouchDpad.Current) return;
        switch (direction)
        {
            case Dir.Up:    TouchDpad.Current.SetUp(pressed);    break;
            case Dir.Down:  TouchDpad.Current.SetDown(pressed);  break;
            case Dir.Left:  TouchDpad.Current.SetLeft(pressed);  break;
            case Dir.Right: TouchDpad.Current.SetRight(pressed); break;
        }
    }
}
