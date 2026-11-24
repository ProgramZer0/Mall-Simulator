using UnityEngine;

/// <summary>
/// Optional, on any text or image on the phone: which of the Customize app's colors it takes. Without it,
/// a designed app (one laid out in the prefab, like Notifications or Franchises) has its texts take the
/// theme's Text color and its buttons the Button color (PhoneScreen → Theme Designed Apps). Keep leaves
/// it as designed.
/// </summary>
public class PhoneThemeRole : MonoBehaviour
{
    public enum Role { Auto, Keep, Text, DimText, Button, Accent, Background, Track }
    public Role role = Role.Auto;
}
