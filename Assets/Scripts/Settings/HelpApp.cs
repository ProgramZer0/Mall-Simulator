using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The phone's Help app (see PhoneScreen): a list of topics; tap one to read it, Back to the list.
/// Topics are edited in the Inspector (rich text works: &lt;b&gt;, colours...). Empty = the built-in
/// ones, which cover the controls and the main systems (right-click the component → Fill With
/// Built-In Topics to start editing from those).
///
/// Setup: an empty panel under the phone's canvas, stretched over the app area, with this on it. The
/// page is built inside it by code (Style sets colours and sizes).
/// </summary>
public class HelpApp : MonoBehaviour, IPhoneApp
{
    [Serializable]
    public class Topic
    {
        public string title;
        [TextArea(4, 20)] public string body;
    }

    [SerializeField] private string appName = "Help";
    [SerializeField] private Sprite icon;
    [SerializeField] private List<Topic> topics = new();
    [SerializeField] private PhoneUI.Style style = new();

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private IReadOnlyList<Topic> Topics => topics != null && topics.Count > 0 ? topics : BuiltIn;

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        ShowList();
    }

    public void OnAppClosed() { }

    private void ShowList()
    {
        PhoneUI.Clear(content);
        float y = style.padding;
        foreach (var topic in Topics)
        {
            var t = topic;
            PhoneUI.Button(content, t.title, y, style.rowHeight, style, () => ShowTopic(t));
            y += style.rowHeight + style.spacing;
        }
        SetHeight(y + style.padding);
    }

    private void ShowTopic(Topic topic)
    {
        PhoneUI.Clear(content);
        float y = style.padding;
        PhoneUI.Button(content, "‹ Back", y, style.rowHeight, style, ShowList, -1f, (content.rect.width > 0f ? content.rect.width : 300f) * 0.6f);
        y += style.rowHeight + style.spacing;
        var title = PhoneUI.Text(content, $"<b>{topic.title}</b>", y, style.rowHeight, style, false, TextAlignmentOptions.Left);
        title.color = style.accent;
        y += style.rowHeight + style.spacing * 0.5f;

        var body = PhoneUI.Text(content, topic.body, y, 10f, style);
        float width = Mathf.Max(50f, content.rect.width - style.padding * 2f);
        float h = body.GetPreferredValues(topic.body, width, 0f).y + 4f;
        var rt = (RectTransform)body.transform;
        rt.offsetMin = new Vector2(rt.offsetMin.x, -y - h);
        y += h + style.padding;
        SetHeight(y);
    }

    private void SetHeight(float h)
    {
        content.sizeDelta = new Vector2(content.sizeDelta.x, h);
        content.anchoredPosition = Vector2.zero; // back to the top
    }

    [ContextMenu("Fill With Built-In Topics")]
    private void FillWithBuiltIn()
    {
        topics = new List<Topic>();
        foreach (var t in BuiltIn) topics.Add(new Topic { title = t.title, body = t.body });
    }

    private static readonly Topic[] BuiltIn =
    {
        new() { title = "Controls", body =
            "<b>WASD</b> move   <b>Mouse</b> look\n<b>Shift</b> sprint   <b>Space</b> jump   <b>Ctrl</b> crouch\n" +
            "<b>F</b> use / pick up / put down   <b>Left click</b> place or click\n" +
            "<b>Esc</b> phone   <b>Tab</b> notifications\n<b>1</b> price scanner   <b>2</b> paint brush\n" +
            "<b>0</b> edit your store   <b>I</b> store checker\n<b>L</b> lock or unlock a door" },
        new() { title = "Your store", body =
            "You start with a small store. Stand at its monitor and press F to order stock, buy upgrades, " +
            "spend perk points and expand.\n\nOpen your store so customers can come in; close it when you need " +
            "the floor to yourself. Buy more stores as your level allows: walk up to one for sale and press F." },
        new() { title = "Ordering and deliveries", body =
            "Everything you order arrives in its own box in your store's shipment area once its delivery time " +
            "is up. If there's no room, it waits at the dock until there is.\n\nDeliveries are held for you for " +
            "10 minutes; after that anyone can take them. You can carry another store's box to them to help out.\n\n" +
            "Carts: press F to push and let go. Things in the cart's bed ride along." },
        new() { title = "Stocking shelves", body =
            "Hold a crate and look at a shelf: <b>F</b> stocks one (the preview shows where), <b>G</b> takes one back " +
            "into the crate. With empty hands, <b>F</b> takes one off the shelf.\n\n<b>R</b> turns the products, " +
            "<b>T</b> lays them down. If a crate won't go, the prompt says why.\n\nPlacing furniture: hold it and use " +
            "<b>,</b> and <b>.</b> to turn it, then click to put it down." },
        new() { title = "Prices", body =
            "Press <b>1</b> for the price scanner and point it at something for sale. Type a price and press " +
            "<b>Enter</b> for that one, <b>Shift+Enter</b> for every one of that product. A price of <b>0</b> takes it off sale.\n\n" +
            "Customers compare your price with what the item is really worth: too high and they walk away, low " +
            "and they buy more but you make less." },
        new() { title = "The checkout", body =
            "Customers line up and put their things on the belt. Click each thing to scan it.\n\n" +
            "<b>Card:</b> take the card, then swipe it in the reader.\n<b>Cash:</b> take the cash, count the change " +
            "out of the drawer (the keypad can work it out for you), then confirm.\n\n" +
            "A customer who waits too long walks off and leaves their things: click them to take them back to the shelves." },
        new() { title = "Customers", body =
            "Customers come in different kinds, each liking different things and caring more or less about price " +
            "and how a store looks. What they can see through your windows and doors decides whether they walk in.\n\n" +
            "Inside, they look for what suits them at a fair price. Keep shelves full, decorate to suit what you " +
            "sell, and keep lines short. Seasons and mall events change how many come and who." },
        new() { title = "Editing your store", body =
            "Press <b>0</b> to edit (the store closes while you do). Move the camera with <b>WASD</b>, turn with " +
            "<b>Q</b>/<b>E</b>, drag things to move them, <b>R</b> to rotate, <b>Ctrl</b> to move freely, " +
            "<b>Delete</b> to sell something back.\n\n<b>B</b> builds walls, <b>H</b> changes their height or shows " +
            "the controls. Windows and doors slide along walls and cut their own holes. Your storefront must keep a door.\n\n" +
            "Press <b>0</b> again to save." },
        new() { title = "Levels and perks", body =
            "Happy customers and profit level your team up, unlocking items, decor and more stores. Unhappy " +
            "customers cost experience.\n\nEach store earns its own perk points from profit; spend them on its " +
            "monitor's Perks tab. You can respec for free, but a store whose layout doesn't match its perks sells less." },
        new() { title = "Licenses, franchises and votes", body =
            "<b>Licenses:</b> some categories need one to order, and only so many exist per mall.\n" +
            "<b>Franchises:</b> win an auction (phone → Franchises) to be the only one allowed to order a category for a while.\n" +
            "<b>Mall votes:</b> every so often a vote opens at the kiosks; the winner changes the whole mall for a while." },
    };
}
