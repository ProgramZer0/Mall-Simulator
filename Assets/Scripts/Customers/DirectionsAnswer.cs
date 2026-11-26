using UnityEngine;

/// <summary>
/// The local player answering a customer's "where's ...?": while it's open, their prompt is the question
/// and the store their crosshair is on; click points it out, right-click says they don't know. Run by
/// PlayerInteractInput (which leaves its other clicks alone meanwhile).
/// </summary>
public static class DirectionsAnswer
{
    private static ushort customer;
    private static string question;
    private static float until;
    private static StorePlot aimed;

    public static bool Active => customer != 0 && Time.time < until;

    public static void Begin(ushort id, string text, float seconds)
    {
        customer = id;
        question = text;
        until = Time.time + seconds;
        aimed = null;
    }

    public static void End(ushort id)
    {
        if (customer == id) customer = 0;
    }

    /// <summary>The prompt while answering.</summary>
    public static string Prompt =>
        $"\"{question}\"\n" + (aimed != null ? $"Click: it's {aimed.DisplayName}" : "Look at the store's front and click") +
        "   Right-click: sorry, I don't know";

    private const float MaxAngle = 22f; // degrees off the crosshair a store may be and still count

    /// <summary>
    /// The open store most nearly in this direction: no need to hit its front, or even see it, just point
    /// the way (up at the floor above, down over the railing at the one below). Within Max Angle.
    /// </summary>
    public static StorePlot StoreInDirection(Vector3 eye, Vector3 forward)
    {
        var mall = MallManager.Instance;
        if (mall == null) return null;
        StorePlot best = null;
        float bestAngle = MaxAngle;
        foreach (var plot in mall.Plots)
        {
            if (plot == null || !plot.HasOwner) continue;
            Bounds b = plot.GetWorldBounds();
            // Its nearest part to the line of sight, as an angle off it (sampled along the line).
            float near = float.MaxValue;
            for (float t = 1f; t <= 90f; t += 1f)
            {
                Vector3 p = eye + forward * t;
                float d = Mathf.Sqrt(b.SqrDistance(p));
                float angle = Mathf.Atan2(d, t) * Mathf.Rad2Deg;
                if (angle < near) near = angle;
                if (d <= 0f) break;
            }
            if (near < bestAngle) { bestAngle = near; best = plot; }
        }
        return best;
    }

    /// <summary>Each frame while Active: which store is in the crosshair, and the clicks.</summary>
    public static void Tick(Camera cam)
    {
        if (!Active) { customer = 0; return; }
        aimed = cam != null ? StoreInDirection(cam.transform.position, cam.transform.forward) : null;
        if (UIState.IsMenuOpen || CustomerManager.Instance == null) return;
        if (Input.GetMouseButtonDown(0) && aimed != null) { CustomerManager.Instance.Answer(customer, aimed.PlotId); customer = 0; }
        else if (Input.GetMouseButtonDown(1)) { CustomerManager.Instance.Answer(customer, -1); customer = 0; }
    }
}
