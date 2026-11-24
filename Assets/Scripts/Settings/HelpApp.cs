using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// The phone's Help app (see PhoneScreen): a list of topics; tap one to read it, Back to the list.
/// The built-in topics cover the controls and the main systems. Topics in the Inspector (rich text
/// works: &lt;b&gt;, colours...) replace the built-in one with the same Id (or Title, when the Id is
/// empty) and come first; built-in ones not listed follow. Right-click the component → Fill With
/// Built-In Topics to start editing from those (or Add Missing Built-In Topics after an update).
///
/// Ids are what first-time tips (HelpTips) open: "Learn more about ..." then the phone opens here.
///
/// Setup: an empty panel under the phone's canvas, stretched over the app area, with this on it. The
/// page is built inside it by code (Style sets colours and sizes).
/// </summary>
public class HelpApp : MonoBehaviour, IPhoneApp
{
    [Serializable]
    public class Topic
    {
        [Tooltip("What first-time tips use to open it (HelpTips). Keep a built-in topic's Id to replace it.")]
        public string id;
        public string title;
        [TextArea(4, 20)] public string body;
        [Tooltip("Optional: a short clip shown above the text, looping, muted (an mp4, H.264, 720p, 10-20 s works well).")]
        public VideoClip video;
        [Tooltip("Optional: the page as blocks, top to bottom (text, a video, more text, a picture...). When there " +
                 "are any, they're the page and Body/Video above aren't used.")]
        public List<Block> blocks = new();
    }

    [System.Serializable]
    public class Block
    {
        public enum Kind { Text, Video, Picture }
        public Kind kind = Kind.Text;
        [TextArea(3, 15)] public string text;
        public VideoClip video;
        public Sprite picture;
    }

    [SerializeField] private string appName = "Help";
    [SerializeField] private Sprite icon;
    [SerializeField] private List<Topic> topics = new();
    [Header("Your own buttons (optional)")]
    [Tooltip("A button (with a TMP text in it for the topic's title) used for each topic in the list instead of " +
             "the built-in one. Its own size and scale are kept; it's only made narrower if it wouldn't fit.")]
    [SerializeField] private Button topicButtonPrefab;
    [Tooltip("The same, for Back on a topic's page (its text is set to Back Label).")]
    [SerializeField] private Button backButtonPrefab;
    [SerializeField] private string backLabel = "‹ Back";
    [Tooltip("Stretch your buttons across the page instead of keeping their own width.")]
    [SerializeField] private bool stretchButtons;
    [SerializeField] private PhoneUI.Style style = new();

    public string AppName => appName;
    public Sprite Icon => icon;
    public int Badge => 0;

    private RectTransform content;
    private IReadOnlyList<Topic> Topics => Merge(topics);

    /// <summary>The phone prefab's Help app (PhoneController sets it), for tips before the phone's made.</summary>
    public static HelpApp Prefab { get; set; }

    /// <summary>A topic's title by its Id (null: no such topic).</summary>
    public static string TitleOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var t in Merge(Prefab != null ? Prefab.topics : null))
            if (IdOf(t) == id) return t.title;
        return null;
    }

    private static string IdOf(Topic t) => !string.IsNullOrWhiteSpace(t.id) ? t.id.Trim() : t.title;

    // The Inspector's topics first, then the built-in ones they don't replace.
    private static List<Topic> Merge(List<Topic> custom)
    {
        var all = new List<Topic>();
        var ids = new HashSet<string>();
        var titles = new HashSet<string>();
        if (custom != null)
            foreach (var t in custom)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.title)) continue;
                all.Add(t);
                ids.Add(IdOf(t));
                titles.Add(t.title.Trim());
            }
        foreach (var t in BuiltIn)
            if (!ids.Contains(t.id) && !titles.Contains(t.title)) all.Add(t);
        return all;
    }

    public void OnAppOpened()
    {
        if (content == null) content = PhoneUI.ScrollPage((RectTransform)transform);
        // Opened from a first-time tip: straight to its topic.
        string tip = HelpTips.TakePending();
        if (tip != null)
            foreach (var t in Topics)
                if (IdOf(t) == tip) { ShowTopic(t); return; }
        ShowList();
    }

    public void OnAppClosed() => StopVideo();

    private void ShowList()
    {
        StopVideo();
        PhoneUI.Clear(content);
        float y = style.padding;
        foreach (var topic in Topics)
        {
            var t = topic;
            if (topicButtonPrefab != null) y += OwnButton(topicButtonPrefab, t.title, y, () => ShowTopic(t)) + style.spacing;
            else
            {
                PhoneUI.Button(content, t.title, y, style.rowHeight, style, () => ShowTopic(t));
                y += style.rowHeight + style.spacing;
            }
        }
        SetHeight(y + style.padding);
    }

    // One of your buttons at this height: its own size and scale (narrowed only to fit the page, or
    // stretched across it). Returns how tall it is on the page.
    private float OwnButton(Button prefab, string label, float y, UnityEngine.Events.UnityAction onClick)
    {
        var b = Instantiate(prefab, content, false);
        var rt = (RectTransform)b.transform;
        var source = (RectTransform)prefab.transform;
        Vector3 scale = source.localScale;
        float sx = Mathf.Approximately(scale.x, 0f) ? 1f : scale.x, sy = Mathf.Approximately(scale.y, 0f) ? 1f : scale.y;
        float room = Mathf.Max(20f, (content.rect.width > 0f ? content.rect.width : 300f) - style.padding * 2f);
        float width = source.rect.width * sx;
        if (stretchButtons || width > room || width <= 0f) width = room;
        float height = source.rect.height * sy;
        if (height <= 0f) height = style.rowHeight;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.localScale = scale;
        rt.sizeDelta = new Vector2(width / sx, height / sy);
        rt.anchoredPosition = new Vector2(style.padding, -y);
        var text = b.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
        b.onClick.AddListener(onClick);
        return height;
    }

    private void ShowTopic(Topic topic)
    {
        StopVideo();
        PhoneUI.Clear(content);
        float y = style.padding;
        if (backButtonPrefab != null) y += OwnButton(backButtonPrefab, backLabel, y, ShowList) + style.spacing;
        else
        {
            PhoneUI.Button(content, backLabel, y, style.rowHeight, style, ShowList, -1f, (content.rect.width > 0f ? content.rect.width : 300f) * 0.6f);
            y += style.rowHeight + style.spacing;
        }
        var title = PhoneUI.Text(content, $"<b>{topic.title}</b>", y, style.rowHeight, style, false, TextAlignmentOptions.Left);
        title.color = PhoneTheme.AccentOf(style);
        y += style.rowHeight + style.spacing * 0.5f;
        if (topic.blocks != null && topic.blocks.Count > 0)
        {
            foreach (var b in topic.blocks)
            {
                if (b == null) continue;
                if (b.kind == Block.Kind.Video && b.video != null) y = ShowVideo(b.video, y);
                else if (b.kind == Block.Kind.Picture && b.picture != null) y = ShowPicture(b.picture, y);
                else if (b.kind == Block.Kind.Text && !string.IsNullOrEmpty(b.text)) y = ShowText(b.text, y);
            }
        }
        else
        {
            if (topic.video != null) y = ShowVideo(topic.video, y);
            y = ShowText(topic.body, y);
        }
        SetHeight(y + style.padding);
    }

    // A block of text across the page, as tall as it needs. Returns the y under it.
    private float ShowText(string text, float y)
    {
        var body = PhoneUI.Text(content, text, y, 10f, style);
        float width = Mathf.Max(50f, content.rect.width - style.padding * 2f);
        float h = body.GetPreferredValues(text, width, 0f).y + 4f;
        var rt = (RectTransform)body.transform;
        rt.offsetMin = new Vector2(rt.offsetMin.x, -y - h);
        return y + h + style.spacing;
    }

    // A picture across the page, at its own shape. Returns the y under it.
    private float ShowPicture(Sprite picture, float y)
    {
        float width = Mathf.Max(50f, (content.rect.width > 0f ? content.rect.width : 300f) - style.padding * 2f);
        float aspect = picture.rect.height > 0f ? picture.rect.width / picture.rect.height : 1f;
        float h = width / aspect;
        var go = new GameObject("Picture", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(content, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(style.padding, -y - h);
        rt.offsetMax = new Vector2(-style.padding, -y);
        var img = go.GetComponent<Image>();
        img.sprite = picture;
        img.preserveAspect = true;
        img.raycastTarget = false;
        return y + h + style.spacing;
    }

    /* ========================= VIDEO ========================= */

    // One player (and picture) per video on the page.
    private readonly List<(VideoPlayer player, RenderTexture texture)> videos = new();

    // The clip across the page (its own shape), playing on a loop with no sound. Returns the y under it.
    private float ShowVideo(VideoClip clip, float y)
    {
        float width = Mathf.Max(50f, (content.rect.width > 0f ? content.rect.width : 300f) - style.padding * 2f);
        float aspect = clip.height > 0 ? clip.width / (float)clip.height : 16f / 9f;
        float h = width / aspect;

        int tw = (int)Mathf.Clamp(clip.width, 16, 1920), th = (int)Mathf.Clamp(clip.height, 16, 1080);
        var texture = new RenderTexture(tw, th, 0) { name = "Help Video" };

        var go = new GameObject("Video", typeof(RectTransform), typeof(RawImage));
        var rt = (RectTransform)go.transform;
        rt.SetParent(content, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(style.padding, -y - h);
        rt.offsetMax = new Vector2(-style.padding, -y);
        var image = go.GetComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;

        var player = go.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.isLooping = true;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.skipOnDrop = true;
        player.targetTexture = texture;
        player.clip = clip;
        player.Play();
        videos.Add((player, texture));
        return y + h + style.spacing;
    }

    private void StopVideo()
    {
        foreach (var (player, texture) in videos)
        {
            if (player != null) player.Stop();
            if (texture != null) texture.Release();
        }
        videos.Clear();
    }

    private void OnDestroy() => StopVideo();

    private void SetHeight(float h)
    {
        content.sizeDelta = new Vector2(content.sizeDelta.x, h);
        content.anchoredPosition = Vector2.zero; // back to the top
    }

    [ContextMenu("Fill With Built-In Topics")]
    private void FillWithBuiltIn()
    {
        topics = new List<Topic>();
        foreach (var t in BuiltIn) topics.Add(new Topic { id = t.id, title = t.title, body = t.body });
    }

    // Built-in topics added in an update, onto the end of the Inspector's list (and Ids filled in on ones
    // that match a built-in by title).
    [ContextMenu("Add Missing Built-In Topics")]
    private void AddMissingBuiltIn()
    {
        topics ??= new List<Topic>();
        foreach (var b in BuiltIn)
        {
            var match = topics.Find(t => t != null && (t.id == b.id || t.title?.Trim() == b.title));
            if (match == null) topics.Add(new Topic { id = b.id, title = b.title, body = b.body });
            else if (string.IsNullOrWhiteSpace(match.id)) match.id = b.id;
        }
    }

    private static readonly Topic[] BuiltIn =
    {
        new() { id = "controls", title = "Controls", body =
            "<b>WASD</b> move   <b>Mouse</b> look\n<b>Shift</b> sprint   <b>Space</b> jump   <b>Ctrl</b> crouch\n" +
            "<b>F</b> use / pick up / put down   <b>Left click</b> place or click\n" +
            "<b>Esc</b> / <b>Tab</b> phone\n<b>1</b> price scanner   <b>2</b> paint brush\n" +
            "<b>0</b> edit your store   <b>I</b> store checker\n<b>L</b> lock or unlock a door\n\n" +
            "The prompt in the middle of the screen always says what <b>F</b> will do with what you're looking at." },
        new() { id = "phone", title = "Your phone", body =
            "<b>Esc</b> or <b>Tab</b> brings the phone up where you left it, or on whatever's new: a lost child opens " +
            "Lost & Found, a new notification opens Notifications. Press again to put it away; <b>Back</b> goes home.\n\n" +
            "<b>Notifications:</b> everything that's happened to you and your store.\n" +
            "<b>Help:</b> this. When something happens for the first time, a tip comes up in the corner: bring the " +
            "phone up while it's there and it opens here on that topic.\n" +
            "<b>Lost & Found:</b> lost children and where their parents are.\n" +
            "<b>Franchises:</b> franchise auctions (unlocks at a higher level).\n" +
            "<b>Settings:</b> sound, mouse, display, and Reset help tips to see the tips again." },
        new() { id = "your-store", title = "Your store", body =
            "You start with a small store. Stand at its monitor and press F to order stock, buy upgrades, " +
            "spend perk points and expand.\n\nOpen your store so customers can come in; close it when you need " +
            "the floor to yourself. Buy more stores as your level allows: walk up to one for sale and press F.\n\n" +
            "Press <b>I</b> for the store checker: what customers think of your store and why." },
        new() { id = "ordering", title = "Ordering and deliveries", body =
            "Order from your store's monitor. Stock comes in crates, each holding several of one product; " +
            "furniture and decor come on their own.\n\n" +
            "Everything arrives in your store's shipment area once its delivery time is up. If there's no room, it " +
            "waits at the dock until there is. Deliveries are held for you for 10 minutes; after that anyone can " +
            "take them. Carrying another store's delivery to them earns a help point." },
        new() { id = "stocking", title = "Stocking shelves", body =
            "Hold a crate and look at a shelf: <b>F</b> stocks one (the preview shows where), <b>G</b> takes one back " +
            "into the crate. With empty hands, <b>F</b> takes one off the shelf.\n\n<b>R</b> turns the products, " +
            "<b>T</b> lays them down. If a crate won't go, the prompt says why.\n\n" +
            "<b>Sharing a shelf:</b> stock a different product on a shelf that already has one and the shelf splits " +
            "in two. A line shows where it'll split before you do; which way depends on where you're standing. " +
            "Clothes hang on racks, folded things go on shelves.\n\n" +
            "A crate with room left can be topped up with more of the same product." },
        new() { id = "placing", title = "Placing things", body =
            "Pick something up with <b>F</b>. A see-through preview shows where it'll go; if it can't go there, " +
            "the prompt says why. Turn it with <b>,</b> and <b>.</b> and click to put it down.\n\n" +
            "You can only place things in your own store. Things need " +
            "something under them, can't go inside other things, and must leave a way through your doors: " +
            "customers who can't get out are let out after a while, and it costs you experience.\n\n" +
            "To move a lot at once, or to build, use store edit mode (<b>0</b>)." },
        new() { id = "carts", title = "Carts", body =
            "Look at your store's cart with empty hands and press <b>F</b> to push it, <b>F</b> again to let go. " +
            "Anything you put in its bed rides along, so it's the quick way to bring several crates from the dock.\n\n" +
            "Carts stop against walls and shelves. Pull too far from a stuck cart and you let go. Only your team " +
            "can use your carts.\n\nLeft out in the hallway, another store can report your cart: put it back in your store or at its " +
            "spot within 3 minutes or it's towed home and you're fined. Moving it somewhere else in the hallway doesn't count." },
        new() { id = "prices", title = "Prices", body =
            "Press <b>1</b> for the price scanner and click something for sale. Type a price and press " +
            "<b>Enter</b> for that one, <b>Shift+Enter</b> for every one of that product in the store. A price of " +
            "<b>0</b> takes it off sale. Esc or right click cancels.\n\n" +
            "Customers compare your price with what the item is really worth on the market: too high and they " +
            "walk away (and shoplifters are more tempted), low and they buy more but you make less. Market " +
            "prices move with events and seasons." },
        new() { id = "checkout", title = "The checkout", body =
            "Customers line up and put their things on the belt. Click each thing to scan it.\n\n" +
            "<b>Card:</b> take the card, then swipe it in the reader.\n<b>Cash:</b> take the cash, count the change " +
            "out of the drawer (the keypad can work it out for you), then confirm.\n\n" +
            "A customer who waits too long walks off and leaves their things: click them to take them back to the " +
            "shelves." },
        new() { id = "customers", title = "Customers", body =
            "Customers come in different kinds, each liking different things and caring more or less about price " +
            "and how a store looks. What they can see through your windows and doors decides whether they walk in.\n\n" +
            "Some come as groups or families and shop together: children stay with their parents. Inside, they " +
            "look for what suits them at a fair price. Keep shelves full, decorate to suit what you sell, and keep " +
            "lines short.\n\n" +
            "How many come to the mall depends on seasons, events, and the mall's reputation: broken lights and " +
            "lost children nobody helped keep shoppers away." },
        new() { id = "shoplifters", title = "Shoplifters", body =
            "Some customers steal, more so when a price is well over what the item is worth. They won't try it " +
            "with a player close by watching. A theft may set off the alarm as they leave (Theft Resistance perks " +
            "make that likelier).\n\n" +
            "Look at a customer and press <b>F</b> to stop them and check their bag. A shoplifter drops what they " +
            "took as crates at their feet: pick them up and put them back. Catching someone stealing from another " +
            "store earns a help point." },
        new() { id = "editing", title = "Editing your store", body =
            "Press <b>0</b> to edit (the store closes while you do). Move the camera with <b>WASD</b>, turn with " +
            "<b>Q</b>/<b>E</b>, drag things to move them, <b>R</b> to rotate, <b>Ctrl</b> to move freely, " +
            "<b>Delete</b> to sell something back.\n\n<b>B</b> builds walls, <b>H</b> changes their height or shows " +
            "the controls. Windows and doors slide along walls and cut their own holes. Your storefront must keep a door.\n\n" +
            "Press <b>0</b> again to save." },
        new() { id = "painting", title = "Painting and colors", body =
            "Tap <b>2</b> (empty hands) for the paint brush; hold <b>2</b> for the color wheel, point at a color and " +
            "let go. Click something of yours to paint it: items that can be painted, walls, the storefront, the " +
            "floor. Each paint job costs a small fee.\n\n" +
            "Colors unlock as your team levels up. Some items only come in certain colors, and a franchise adds its " +
            "own colors to the items it covers. Customers like some colors better on some things, and some kinds of " +
            "customer have favorite colors." },
        new() { id = "levels", title = "Levels and perks", body =
            "Happy customers and profit level your team up, unlocking items, colors, decor and more stores. " +
            "Unhappy customers cost experience.\n\nEach store earns its own perk points from profit; spend them on its " +
            "monitor's Perks tab. You can respec for free, but a store whose layout doesn't match its perks sells less." },
        new() { id = "licenses", title = "Licenses", body =
            "Some categories need a license before you can order them, and only so many licenses exist per mall. " +
            "Get one from your monitor while there's one free.\n\n" +
            "Giving up a license you never ordered anything with earns a help point (someone else can use it)." },
        new() { id = "franchises", title = "Franchises", body =
            "A franchise makes your team the only one allowed to order its category for a while, and adds the " +
            "brand's colors to its items.\n\n" +
            "Franchises are won at auction: phone → Franchises (unlocks at a higher level). The highest bid when " +
            "the auction ends wins. When it runs out, or its holder gives it up, bidding opens again." },
        new() { id = "votes", title = "Mall votes", body =
            "Every so often a vote opens at the mall's kiosks. Look at an option's button and press <b>F</b> to " +
            "vote (again to change your vote). When it closes, the winner changes the whole mall for a while.\n\n" +
            "Help points make your vote count more: see Help points." },
        new() { id = "events", title = "Mall events and seasons", body =
            "Seasons run one after another, and events come and go on top of them. They change how many " +
            "customers come and who, how much they spend, what things cost to order and sell for, even how " +
            "many shoplifters there are.\n\n" +
            "What's running now is shown on screen. Stock up on what an event makes popular, and watch your " +
            "prices when market prices move." },
        new() { id = "help-points", title = "Help points", body =
            "Helping the mall earns help points:\n" +
            "• bringing a lost child back to their family\n• reporting something broken\n" +
            "• stopping a shoplifter in another team's store\n• delivering another store's order\n" +
            "• giving up a license you didn't use\n\n" +
            "Each kind only counts a few times per vote. Help points make your mall vote count more; the ones " +
            "that did are used up when the vote closes." },
        new() { id = "lost-children", title = "Lost children", body =
            "Now and then a child wanders away from their family. Everyone's told, and the family stops shopping " +
            "to look for them.\n\n" +
            "Phone → <b>Lost & Found</b> shows a live picture of the child and of their parents, so you can work out " +
            "where they are. Find the child and press <b>F</b>: they follow you. Stay close, or they'll lose you and " +
            "wait where they stopped. Bring them to their parents for a help point and experience.\n\n" +
            "A child nobody helps eventually finds their family alone, but word gets around: fewer shoppers come " +
            "to the mall for a while." },
        new() { id = "broken", title = "Broken lights and screens", body =
            "Things in the mall break now and then: a light flickers or goes out, a screen cracks. Broken things " +
            "keep shoppers away from the whole mall, for as long as they're broken.\n\n" +
            "Spot one, look at it (from a distance is fine) and press <b>F</b> to report it: you get a help point and " +
            "maintenance fixes it shortly. If nobody does, shoppers start complaining." },
    };
}
