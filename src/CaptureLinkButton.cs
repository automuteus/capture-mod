using System;
using InnerNet;
using UnityEngine;

namespace CaptureMod;

// The "Paste capture link" button, with a status line under it. It only shows on the main menu and in the lobby, so
// it's never in the way of a game. It reads the clipboard because players copy the link from Discord anyway.
//
// Drawn with Unity's IMGUI rather than by copying one of the game's buttons, which would tie it to the menus' layout
// and break with game updates. Its own styles keep it close to the game's look without touching other mods' GUI.
public class CaptureLinkButton : MonoBehaviour
{
    static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(5);

    static readonly Color Good = new(0.35f, 0.85f, 0.4f);
    static readonly Color Waiting = new(1f, 0.75f, 0.2f);
    static readonly Color Bad = new(1f, 0.38f, 0.38f);

    // Where the button was last drawn, in GUI coordinates (origin top left), or null while it's hidden.
    static Rect? drawnAt;

    GUIStyle? buttonStyle, statusStyle, panelStyle, goodStripe, waitingStripe, badStripe;
    string? notice;
    DateTime noticeUntil;

    public CaptureLinkButton(IntPtr ptr) : base(ptr)
    {
    }

    void OnGUI()
    {
        var client = AmongUsClient.Instance;
        if (client != null && client.GameState is InnerNetClient.GameStates.Started or InnerNetClient.GameStates.Ended)
        {
            drawnAt = null;
            return;
        }
        if (buttonStyle is null) CreateStyles();

        // Sized from the screen height, so it reads the same at any resolution.
        var unit = Screen.height / 1080f;
        buttonStyle!.fontSize = statusStyle!.fontSize = (int)(22 * unit);
        statusStyle.padding = new RectOffset
            { left = (int)(10 * unit), right = (int)(12 * unit), top = (int)(6 * unit), bottom = (int)(6 * unit) };
        var (text, color) = DateTime.UtcNow < noticeUntil ? (notice!, Bad) : Status();

        var x = 20 * unit;
        var gap = 6 * unit;
        var stripe = 6 * unit;
        var button = new Rect(x, 0, 320 * unit, 52 * unit);
        var statusSize = statusStyle.CalcSize(new GUIContent(text));
        var status = new Rect(x, 0, statusSize.x + stripe, statusSize.y);
        status.y = Screen.height - 20 * unit - status.height;
        button.y = status.y - gap - button.height;

        drawnAt = button;
        if (GUI.Button(button, "Paste capture link", buttonStyle)) Paste();

        // The colored stripe and text say at a glance whether the bot can hear this game. The game's build strips
        // GUI.DrawTexture, so panels are labels with a background.
        GUI.Label(status, "", panelStyle);
        var stripeStyle = color == Good ? goodStripe : color == Waiting ? waitingStripe : badStripe;
        GUI.Label(new Rect(status.x, status.y, stripe, status.height), "", stripeStyle);
        statusStyle.normal.textColor = color;
        GUI.Label(new Rect(status.x + stripe, status.y, statusSize.x, status.height), text, statusStyle);
    }

    void CreateStyles()
    {
        var panel = Solid(new Color(0.08f, 0.08f, 0.1f, 0.85f));
        panelStyle = Background(panel);
        goodStripe = Background(Solid(Good));
        waitingStripe = Background(Solid(Waiting));
        badStripe = Background(Solid(Bad));

        buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        SetState(buttonStyle.normal, panel, Color.white);
        SetState(buttonStyle.hover, Solid(new Color(0.18f, 0.18f, 0.22f, 0.92f)), Color.white);
        SetState(buttonStyle.active, Solid(new Color(0.28f, 0.28f, 0.34f, 0.95f)), Color.white);

        statusStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false
        };
    }

    static GUIStyle Background(Texture2D texture)
    {
        // Copied from a built-in style rather than new GUIStyle(), the one constructor already known to survive stripping.
        var style = new GUIStyle(GUI.skin.label);
        style.normal.background = texture;
        return style;
    }

    static void SetState(GUIStyleState state, Texture2D background, Color text)
    {
        state.background = background;
        state.textColor = text;
    }

    static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        // Kept across scene loads, which otherwise unload textures nothing in the scene references.
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }

    // The game handles its own buttons without knowing about this one, so it would also click whatever is under it.
    public static bool UnderMouse()
    {
        if (drawnAt is not { } rect) return false;
        var mouse = Input.mousePosition;
        return rect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
    }

    void Paste()
    {
        var link = CaptureLink.Parse(GUIUtility.systemCopyBuffer ?? "");
        if (link is null)
        {
            notice = "No capture link on the clipboard. Copy the aucapture:// link from /new.";
            noticeUntil = DateTime.UtcNow + NoticeDuration;
            return;
        }
        noticeUntil = DateTime.MinValue;
        CapturePlugin.Connect(link);
    }

    static (string, Color) Status()
    {
        var galactus = CapturePlugin.Galactus;
        if (galactus is null) return ("AutoMuteUs: not connected", Bad);
        if (galactus.Replaced) return ("AutoMuteUs: another capture took over this game", Bad);
        return galactus.Connected ? ("AutoMuteUs: connected", Good) : ("AutoMuteUs: connecting…", Waiting);
    }
}

[HarmonyLib.HarmonyPatch(typeof(PassiveButtonManager), nameof(PassiveButtonManager.Update))]
static class PassiveButtonManagerPatch
{
    static bool Prefix() => !CaptureLinkButton.UnderMouse();
}
