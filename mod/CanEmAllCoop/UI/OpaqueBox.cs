using System;
using UnityEngine;

namespace CanEmAllCoop.UI;

/// <summary>
/// A readable text box built only from GUI.Box (other IMGUI calls are stripped in this build). The skin's box texture is
/// translucent, so the background is stacked a few times to get an (almost) solid colour, and the text is drawn on the last layer.
/// The interop has no GUIStyle copy constructor: the skin's box style is changed for the call and put back.
/// </summary>
internal static class OpaqueBox
{
    const int Layers = 6;

    public static void Draw(Rect rect, string text, int fontSize, Color background, Color content, bool bold, bool wrap)
    {
        var st = GUI.skin.box;
        int oldSize = st.fontSize;
        var oldStyle = st.fontStyle;
        var oldAlign = st.alignment;
        bool oldWrap = st.wordWrap;
        var oldBg = GUI.backgroundColor;
        var oldContent = GUI.contentColor;
        try
        {
            st.fontSize = fontSize;
            st.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            st.alignment = TextAnchor.MiddleCenter;
            st.wordWrap = wrap;
            GUI.backgroundColor = background;
            GUI.contentColor = content;
            for (int i = 0; i < Layers - 1; i++) GUI.Box(rect, "", st);
            GUI.Box(rect, text, st);
        }
        finally
        {
            st.fontSize = oldSize;
            st.fontStyle = oldStyle;
            st.alignment = oldAlign;
            st.wordWrap = oldWrap;
            GUI.backgroundColor = oldBg;
            GUI.contentColor = oldContent;
        }
    }
}
