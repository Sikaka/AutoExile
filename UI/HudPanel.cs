using System.Numerics;
using Hexa.NET.ImGui;

namespace AutoExile.UI
{
    /// <summary>
    /// The in-game status panel. It is drawn with the ImGui draw list inside a window that takes no
    /// input; a separate 16 px window holds the expand toggle and is the only clickable area, because
    /// the bot moves the mouse over the game all the time.
    /// Layout numbers are in design units (8 px font advance, 16 px row pitch) and are scaled to the
    /// font ExileApi actually loaded.
    /// </summary>
    public sealed class HudPanel
    {
        private static readonly Vector4 WinBg = new(0.05f, 0.06f, 0.08f, 0.82f);
        private static readonly Vector4 WinBrd = new(1f, 1f, 1f, 0.09f);
        private static readonly Vector4 ShadowCol = new(0f, 0f, 0f, 0.35f);
        private static readonly Vector4 Bright = new(0.92f, 0.93f, 0.95f, 1f);
        private static readonly Vector4 Mid = new(0.74f, 0.77f, 0.82f, 1f);
        private static readonly Vector4 Dim = new(0.50f, 0.54f, 0.61f, 1f);
        private static readonly Vector4 DividerCol = new(1f, 1f, 1f, 0.07f);
        private static readonly Vector4 CellSep = new(0.92f, 0.93f, 0.95f, 0.12f);
        private static readonly Vector4 Green = new(0.38f, 0.86f, 0.50f, 1f);
        private static readonly Vector4 Yellow = new(0.98f, 0.79f, 0.30f, 1f);
        private static readonly Vector4 Red = new(0.96f, 0.40f, 0.36f, 1f);
        private static readonly Vector4 Cyan = new(0.38f, 0.80f, 0.95f, 1f);
        private static readonly Vector4 Gold = new(0.94f, 0.77f, 0.40f, 1f);
        private static readonly Vector4 Amber = new(0.98f, 0.68f, 0.26f, 1f);
        private static readonly Vector4 DecisionCol = new(0.93f, 0.86f, 0.60f, 1f);
        private static readonly Vector4 SeasonLbl = new(0.80f, 0.67f, 0.42f, 1f);
        private static readonly Vector4 SegDone = new(0.66f, 0.71f, 0.79f, 0.50f);
        private static readonly Vector4 SegNext = new(1f, 1f, 1f, 0.09f);
        private static readonly Vector4 Chev = new(0.80f, 0.83f, 0.88f, 0.90f);

        private const float ContentW = 340f, PadX = 12f, PadY = 8f, PanelW = ContentW + 2 * PadX - 4f;
        /// <summary>Row pitch relative to the font height; 1.0 looked cramped in game.</summary>
        private const float RowSpacing = 1.2f;
        // Column anchors derived from the content width.
        private const float ToggleX = ContentW - 16f, ChipX = ContentW - 52f, GaugeX = 88f, GaugeW = ChipX - 8f - GaugeX;
        private const float ColValue = ContentW / 2f - 8f, ColSep = ContentW / 2f + 0.5f, ColLabel = ContentW / 2f + 8f;
        private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground;
        private const ImGuiWindowFlags ToggleFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing;

        private ImDrawListPtr _dl;
        private Vector2 _o;
        private float _sx = 1f, _sy = 1f, _lineH = 16f;

        /// <summary>
        /// Draws the panel with its top-left corner at <paramref name="pos"/> and returns the pixel y of its
        /// bottom edge. Expanded details shrink to stay above <paramref name="maxBottom"/>. The toggle is
        /// shown only when <paramref name="toggle"/> is set.
        /// </summary>
        public float Draw(Vector2 pos, float maxBottom, HudHeader header, SimulacrumHud? sim, bool expanded, Action? toggle)
        {
            _lineH = ImGui.GetTextLineHeight();
            _sx = Math.Max(0.5f, ImGui.CalcTextSize("0").X / 8f);
            var baseSy = Math.Max(1f, _lineH / 16f);
            var details = sim != null && expanded;

            // Fit above maxBottom (the chat): first drop the extra row spacing, then cut the decision to
            // one line, then drop the monsters/ignored row.
            var showLive = true;
            var decision = details ? Wrap(sim!.Decision, ContentW, 2) : Array.Empty<string>();
            IReadOnlyList<string> warnings = Array.Empty<string>();
            float bodyBottom = 0f, bottom = 0f;
            var rows = 0;
            for (var step = 0; step < 4; step++)
            {
                _sy = baseSy * (step == 0 ? RowSpacing : 1f);
                if (step == 2 && decision.Length > 1) decision = Wrap(sim!.Decision, ContentW, 1);
                if (step == 3) showLive = false;

                // Without the details row, the ignored-item count shows as a warning instead.
                warnings = sim == null ? Array.Empty<string>()
                    : sim.Ignored > 0 && !(details && showLive) ? sim.Warnings.Append($"Ignored items: {sim.Ignored}").ToList()
                    : sim.Warnings;
                rows = warnings.Count + (header.RecordingTicks.HasValue ? 1 : 0);
                bodyBottom = sim != null
                    ? (rows == 0 ? 86f : 92f + 18f * rows - 2f)
                    : (rows == 0 ? 18f : 28f + 18f * rows - 2f);
                bottom = details ? ExpandedBottom(bodyBottom, showLive, decision.Length) : bodyBottom;
                if (pos.Y + (bottom + 2 * PadY) * _sy <= maxBottom) break;
            }
            var size = new Vector2(PanelW * _sx, (bottom + 2 * PadY) * _sy);
            _o = pos + new Vector2(PadX * _sx, PadY * _sy);

            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size + new Vector2(2f, 2f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            if (ImGui.Begin("##aeHud", PanelFlags))
            {
                _dl = ImGui.GetWindowDrawList();
                var stateCol = header.Running ? Green : Yellow;
                _dl.AddRectFilled(pos + new Vector2(2f, 2f), pos + size + new Vector2(2f, 2f), U(ShadowCol), 5f);
                _dl.AddRectFilled(pos, pos + size, U(WinBg), 5f);
                _dl.AddRect(pos + new Vector2(0.5f, 0.5f), pos + size - new Vector2(0.5f, 0.5f), U(WinBrd), 5f);
                _dl.AddRectFilled(pos + new Vector2(3f, 6f), pos + new Vector2(5f, size.Y - 6f), U(stateCol, 0.9f), 1f);

                DrawHeader(header, toggle != null);
                var y = sim != null ? DrawSimulacrumBody(sim, warnings, header.Running) : 28f;
                if (sim == null && rows > 0) HLine(23.5f);
                if (header.RecordingTicks.HasValue) RecRow(y, header.RecordingTicks.Value, header.Running);
                if (sim != null && expanded) DrawDetails(sim, bodyBottom, showLive, decision);
                ImGui.Dummy(size + new Vector2(2f, 2f));
            }
            ImGui.End();
            ImGui.PopStyleVar(3);

            if (toggle != null) DrawToggle(expanded, toggle);
            return pos.Y + size.Y;
        }

        /// <summary>
        /// One-line status pill centred at the top, used while a left-side game panel (stash) is open so
        /// the full panel does not cover it. Takes no input. Returns the pixel y of its bottom edge.
        /// </summary>
        public float DrawPill(float centerX, float top, HudHeader header, SimulacrumHud? sim)
        {
            _lineH = ImGui.GetTextLineHeight();
            _sx = Math.Max(0.5f, ImGui.CalcTextSize("0").X / 8f);
            _sy = Math.Max(1f, _lineH / 16f);

            var parts = new List<(string Text, Vector4 Col)> { (header.Mode.ToUpperInvariant(), Bright) };
            if (sim != null)
            {
                parts.Add(("  W", Dim));
                parts.Add((sim.Wave.ToString(), sim.WaveActive ? Red : Cyan));
                parts.Add(("/" + sim.WaveMax, Dim));
                parts.Add(("  ", Dim));
                parts.Add((sim.RunTime, Bright));
                if (sim.Deaths > 0)
                {
                    parts.Add(("  DTH ", Dim));
                    parts.Add((sim.Deaths.ToString(), Red));
                }
            }
            else
            {
                parts.Add(("  ", Dim));
                parts.Add((header.Elapsed, Mid));
            }

            const float padX = 9f, padY = 3f;
            var textW = parts.Sum(p => W(p.Text));
            var size = new Vector2((padX + 12f + textW + padX) * _sx, (16f + 2f * padY) * _sy);
            var pos = new Vector2(MathF.Round(centerX - size.X / 2f), top);
            _o = pos + new Vector2(padX * _sx, padY * _sy);

            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            if (ImGui.Begin("##aeHudPill", PanelFlags))
            {
                _dl = ImGui.GetWindowDrawList();
                var stateCol = header.Running ? Green : Yellow;
                var radius = size.Y / 2f;
                _dl.AddRectFilled(pos, pos + size, U(WinBg), radius);
                _dl.AddRect(pos + new Vector2(0.5f, 0.5f), pos + size - new Vector2(0.5f, 0.5f), U(stateCol, 0.45f), radius);
                _dl.AddCircleFilled(At(3f, 8f), 3f * _sx, U(stateCol, Pulse(header.Running)));
                var x = 12f;
                foreach (var (text, col) in parts) x = TextRun(x, 0f, 16f, text, col);
                ImGui.Dummy(size);
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            return pos.Y + size.Y;
        }

        private static float ExpandedBottom(float b, bool live, int decisionLines)
        {
            var cursor = live ? b + 28f : b + 8f;
            if (decisionLines > 0) cursor += 20f + 16f * decisionLines;
            return cursor + 84f;
        }

        private void DrawHeader(HudHeader h, bool hasToggle)
        {
            var stateCol = h.Running ? Green : Yellow;
            Chip(0f, 0f, 76f, 18f, stateCol);
            _dl.AddCircleFilled(At(9f, 9f), 3f * _sx, U(stateCol, Pulse(h.Running)));
            Text(17f, 0f, 18f, h.Running ? "RUNNING" : "PAUSED", stateCol);

            var rtRight = hasToggle ? ToggleX - 8f : ContentW;
            var mode = h.Mode.ToUpperInvariant();
            var elapsedW = W(h.Elapsed);
            var modeMax = rtRight - 86f - 8f - elapsedW;
            if (W(mode) > modeMax) mode = Truncate(mode, modeMax);
            Text(86f, 0f, 18f, mode, Bright);

            var avail = rtRight - (86f + W(mode) + 8f);
            string? limit = null;
            if (h.Limit != null)
            {
                if (elapsedW + W(" / " + h.Limit) <= avail) limit = " / " + h.Limit;
                else if (elapsedW + W("/" + h.Limit) <= avail) limit = "/" + h.Limit;
            }
            var x = rtRight - elapsedW - (limit != null ? W(limit) : 0f);
            x = TextRun(x, 0f, 18f, h.Elapsed, Bright);
            if (limit != null) Text(x, 0f, 18f, limit, h.LimitCritical ? Red : h.LimitLow ? Amber : Dim);
        }

        private float DrawSimulacrumBody(SimulacrumHud s, IReadOnlyList<string> warnings, bool running)
        {
            HLine(23.5f);

            // Wave row: number, 15-segment gauge, ACTIVE/IDLE chip.
            Text(0f, 28f, 16f, "WAVE", Dim);
            var x = TextRun(40f, 28f, 16f, s.Wave.ToString(), Bright);
            Text(x, 28f, 16f, "/" + s.WaveMax, Dim);
            var waveCol = s.WaveActive ? Red : Cyan;
            if (s.WaveMax > 0)
            {
                var n = s.WaveMax;
                var segW = MathF.Max(1f, MathF.Floor((GaugeW - 2f * (n - 1)) / n));
                for (var i = 1; i <= n; i++)
                {
                    var sx = GaugeX + (i - 1) * (segW + 2f);
                    var col = s.Wave == 0 || i > s.Wave ? U(SegNext)
                        : i < s.Wave ? U(SegDone)
                        : U(waveCol, Pulse(running && s.WaveActive));
                    _dl.AddRectFilled(At(sx, 33f), At(sx + segW, 39f), col, 1f);
                }
            }
            Chip(ChipX, 28f, ContentW, 44f, waveCol);
            var chip = s.WaveActive ? "ACTIVE" : "IDLE";
            Text(ChipX + (52f - W(chip)) / 2f, 28f, 16f, chip, waveCol);

            // Telemetry row: run time | deaths | loot.
            x = TextRun(0f, 50f, 16f, "RUN ", Dim);
            Text(x, 50f, 16f, s.RunTime, Bright);
            VLine(104.5f, 53f, 63f);
            if (s.Deaths > 0) _dl.AddRectFilled(At(108f, 50f), At(184f, 66f), U(Red, 0.14f), 2f);
            var atMax = s.MaxDeaths > 0 && s.Deaths >= s.MaxDeaths;
            x = TextRun(114f, 50f, 16f, "DTH ", atMax ? Red : Dim);
            x = TextRun(x, 50f, 16f, s.Deaths.ToString(), s.Deaths > 0 ? Red : Bright);
            Text(x, 50f, 16f, "/" + s.MaxDeaths, Dim);
            VLine(188.5f, 53f, 63f);
            _dl.AddTriangleFilled(At(201f, 54f), At(205f, 58f), At(201f, 62f), U(Gold));
            _dl.AddTriangleFilled(At(201f, 54f), At(201f, 62f), At(197f, 58f), U(Gold));
            _dl.PushClipRect(At(210f, 48f), At(ContentW, 68f), true);
            if (s.RunItems is int items)
            {
                x = TextRun(210f, 50f, 16f, items.ToString(), Bright);
                x = TextRun(x, 50f, 16f, " · ", Dim);
                x = TextRun(x, 50f, 16f, s.RunValue, Gold);
                Text(x, 50f, 16f, s.RunValueUnit, Dim);
            }
            else Text(210f, 50f, 16f, "--", Dim);
            _dl.PopClipRect();

            // Phase and live status.
            var phase = s.Phase.ToUpperInvariant();
            if (phase.Length > 12) phase = phase[..12];
            Text(0f, 70f, 16f, phase, Dim);
            var px = W(phase) + 5f;
            _dl.AddTriangleFilled(At(px, 74.5f), At(px + 4f, 78f), At(px, 81.5f), U(Mid, 0.8f));
            var statusX = px + 11f;
            Text(statusX, 70f, 16f, Truncate(s.Status, ContentW - statusX), Mid);

            var y = 92f;
            foreach (var warning in warnings)
            {
                _dl.AddRectFilled(At(0f, y), At(ContentW, y + 16f), U(Amber, 0.12f), 2f);
                _dl.AddRectFilled(At(0f, y), At(2f, y + 16f), U(Amber, 0.9f));
                Text(8f, y, 16f, Truncate(warning, ContentW - 8f), Amber);
                y += 18f;
            }
            return y;
        }

        private void RecRow(float y, long ticks, bool running)
        {
            _dl.AddRectFilled(At(0f, y), At(ContentW, y + 16f), U(Red, 0.10f), 2f);
            _dl.AddCircleFilled(At(6f, y + 8f), 3f * _sx, U(Red, Pulse(running)));
            var x = TextRun(14f, y, 16f, "REC ", Red);
            Text(x, y, 16f, $"{ticks} ticks", Mid);
        }

        private void DrawDetails(SimulacrumHud s, float b, bool showLive, string[] decision)
        {
            HLine(b + 6.5f);
            var cursor = b + 8f;
            if (showLive)
            {
                var y = b + 12f;
                Text(0f, y, 16f, "MONSTERS", Dim);
                TextRight(ColValue, y, s.Monsters.ToString(), Bright);
                VLine(ColSep, y + 3f, y + 13f);
                Text(ColLabel, y, 16f, "IGNORED", Dim);
                TextRight(ContentW, y, s.Ignored.ToString(), s.Ignored > 0 ? Amber : Bright);
                cursor = b + 28f;
            }
            if (decision.Length > 0)
            {
                Text(0f, cursor + 4f, 16f, "DECISION", Dim);
                for (var i = 0; i < decision.Length; i++)
                    Text(0f, cursor + 20f + 16f * i, 16f, decision[i], DecisionCol);
                cursor += 20f + 16f * decision.Length;
            }

            HLine(cursor + 6.5f);
            var sy = cursor + 12f;
            var x = TextRun(0f, sy, 16f, s.TotalsLabel, SeasonLbl);
            if (!string.IsNullOrEmpty(s.League))
            {
                x = TextRun(x, sy, 16f, " · ", Dim);
                Text(x, sy, 16f, Truncate(s.League.ToUpperInvariant(), ContentW - 90f - x), Mid);
            }
            TextRight(ContentW, sy, s.TotalsTime, Mid);

            GridRow(cursor + 32f, "RUNS", s.Runs, null, "FULL CLEARS", s.FullClears, null);
            GridRow(cursor + 50f, "AVG WAVES", s.AvgWaves, null, "AVG RUN", s.AvgRun, null);
            GridRow(cursor + 68f, "VALUE", s.Value, s.ValueUnit, "RATE", s.Rate, s.RateUnit);
        }

        private void GridRow(float y, string l1, string v1, string? u1, string l2, string v2, string? u2)
        {
            Text(0f, y, 16f, l1, Dim);
            Compound(ColValue, y, v1, u1);
            VLine(ColSep, y + 3f, y + 13f);
            Text(ColLabel, y, 16f, l2, Dim);
            Compound(ContentW, y, v2, u2);
        }

        /// <summary>A right-aligned number with an optional unit; numbers with a unit are money, so gold.</summary>
        private void Compound(float right, float y, string value, string? unit)
        {
            var x = right - W(value) - (unit != null ? W(unit) : 0f);
            x = TextRun(x, y, 16f, value, unit != null ? Gold : Bright);
            if (unit != null) Text(x, y, 16f, unit, Dim);
        }

        private void DrawToggle(bool expanded, Action toggle)
        {
            var size = new Vector2(16f * _sx, 16f * _sy);
            ImGui.SetNextWindowPos(At(ToggleX, 1f));
            ImGui.SetNextWindowSize(size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            if (ImGui.Begin("##aeHudToggle", ToggleFlags))
            {
                // Fires on press, not release: the bot moves the cursor away between the two.
                ImGui.InvisibleButton("##t", size);
                var hovered = ImGui.IsItemHovered();
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) toggle();
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(At(ToggleX, 1f), At(ContentW, 17f), U(new Vector4(1f, 1f, 1f, hovered ? 0.12f : 0.06f)), 3f);
                dl.AddRect(At(ToggleX, 1f), At(ContentW, 17f), U(new Vector4(1f, 1f, 1f, hovered ? 0.35f : 0.18f)), 3f);
                if (expanded)
                    dl.AddTriangleFilled(At(ToggleX + 4.5f, 11f), At(ToggleX + 11.5f, 11f), At(ToggleX + 8f, 7f), U(Chev));
                else
                    dl.AddTriangleFilled(At(ToggleX + 4.5f, 7f), At(ToggleX + 11.5f, 7f), At(ToggleX + 8f, 11f), U(Chev));
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
        }

        // ---- primitives (design units relative to the content origin) ----

        private Vector2 At(float x, float y) => new(_o.X + x * _sx, _o.Y + y * _sy);

        private float W(string text) => string.IsNullOrEmpty(text) ? 0f : ImGui.CalcTextSize(text).X / _sx;

        private void Text(float x, float rowY, float rowH, string text, Vector4 col)
        {
            if (string.IsNullOrEmpty(text)) return;
            var p = At(x, rowY);
            p.Y = MathF.Floor(p.Y + (rowH * _sy - _lineH) * 0.5f);
            _dl.AddText(p, U(col), text);
        }

        private float TextRun(float x, float rowY, float rowH, string text, Vector4 col)
        {
            Text(x, rowY, rowH, text, col);
            return x + W(text);
        }

        private void TextRight(float right, float y, string text, Vector4 col) => Text(right - W(text), y, 16f, text, col);

        private void Chip(float x0, float y0, float x1, float y1, Vector4 col)
        {
            _dl.AddRectFilled(At(x0, y0), At(x1, y1), U(col, 0.16f), 3f);
            _dl.AddRect(At(x0, y0), At(x1, y1), U(col, 0.50f), 3f);
        }

        private void HLine(float y) => _dl.AddLine(At(0f, y), At(ContentW, y), U(DividerCol), 1f);

        private void VLine(float x, float y0, float y1) => _dl.AddLine(At(x, y0), At(x, y1), U(CellSep), 1f);

        private string Truncate(string text, float maxW)
        {
            if (string.IsNullOrEmpty(text) || W(text) <= maxW) return text ?? "";
            for (var n = text.Length - 1; n > 0; n--)
            {
                var cut = text[..n].TrimEnd() + "...";
                if (W(cut) <= maxW) return cut;
            }
            return "...";
        }

        /// <summary>Word-wraps to at most <paramref name="maxLines"/> lines; the last line ends with "..." when text is left.</summary>
        private string[] Wrap(string text, float maxW, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var i = 0;
            while (i < words.Length && lines.Count < maxLines)
            {
                if (lines.Count == maxLines - 1)
                {
                    lines.Add(Truncate(string.Join(' ', words[i..]), maxW));
                    return lines.ToArray();
                }
                var line = words[i++];
                while (i < words.Length && W(line + " " + words[i]) <= maxW) line += " " + words[i++];
                lines.Add(Truncate(line, maxW));
            }
            return lines.ToArray();
        }

        private static float Pulse(bool on) => on
            ? 0.6f + 0.4f * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 % 1000 / 1000f * MathF.PI * 2f))
            : 1f;

        private static uint U(Vector4 col, float alpha) => ImGui.ColorConvertFloat4ToU32(col with { W = col.W * alpha });

        private static uint U(Vector4 col) => ImGui.ColorConvertFloat4ToU32(col);
    }
}
