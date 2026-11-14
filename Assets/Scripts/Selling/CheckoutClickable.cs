using UnityEngine;

/// <summary>
/// A part of a checkout counter the player clicks (left mouse, hands empty): a keypad key, a cash
/// drawer slot, the confirm button, the card reader. Put it on a child object of the counter with a
/// collider (not a trigger). The counter adds its own to the things on the belt and the customer's
/// cash or card.
///
/// Actions:
///   Keypad Digit (Value 0-9), Keypad Double Zero, Keypad Clear, Keypad Enter: the register keypad.
///     Typing is in cents like a real till: 2 5 0 0 = $25.00. Enter shows the change due.
///   Drawer (Value = cents, e.g. 2000 for $20, 25 for a quarter): takes one out as change (drawer open).
///   Drawer Toggle: opens or closes the cash drawer (a button, or the drawer's front).
///   Clear Change: puts the change counted so far back in the drawer.
///   Confirm: finishes a cash sale.
///   Card Reader: swipe the customer's card (after taking it).
/// </summary>
public class CheckoutClickable : MonoBehaviour
{
    public enum Action
    {
        KeypadDigit,
        KeypadDoubleZero,
        KeypadClear,
        KeypadEnter,
        Drawer,
        ClearChange,
        Confirm,
        CardReader,
        DrawerToggle,
        /// <summary>Added by the counter: a thing on the belt (Value = its key).</summary>
        ScanItem,
        /// <summary>Added by the counter: the customer's cash or card.</summary>
        CustomerMoney,
        /// <summary>Added by the counter: a bill or coin of the change counted out (Value = cents); back to the drawer.</summary>
        ReturnChange,
        /// <summary>Added by the counter: a pile of the cash you've taken (Value = cents); hover shows how many.</summary>
        TakenCash,
    }

    public Action action;
    [Tooltip("Keypad Digit: the digit. Drawer: the bill or coin in cents (2000 = $20).")]
    public int value;

    private CheckoutCounter counter;

    /// <summary>The counter this belongs to.</summary>
    public CheckoutCounter Counter
    {
        get
        {
            if (counter == null) counter = GetComponentInParent<CheckoutCounter>();
            return counter;
        }
        set => counter = value;
    }
}
