using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using SDL2;

namespace YAFC.UI
{
    public enum ImGuiAction
    {
        Consumed,
        Build,
        MouseMove,
        MouseDown,
        MouseUp,
        MouseScroll,
        MouseDrag
    }

    public interface IPanel
    {
        void MouseDown(int button);
        void MouseUp(int button);
        void MouseMove(int mouseDownButton);
        void MouseScroll(int delta);
        void MarkEverythingForRebuild();
        Vector2 CalculateState(float width, float pixelsPerUnit);
        void Present(Window window, Rect position, Rect screenClip, ImGui parent);
        IPanel HitTest(Vector2 position);
        void MouseExit();
        bool mouseCapture { get; }
        bool valid { get; }
    }

    public interface IRenderable
    {
        void Render(IntPtr renderer, SDL.SDL_Rect position, SDL.SDL_Color color);
    }

    public enum RectAllocator
    {
        Stretch,
        LeftAlign,
        RightAlign,
        Center,
        LeftRow,
        RightRow,
        RemainigRow,
        FixedRect,
        HalfRow
    }

    public delegate void GuiBuilder(ImGui gui);
    
    public sealed partial class ImGui : IDisposable, IPanel
    {
        public ImGui(GuiBuilder gui, Padding padding, RectAllocator defaultAllocator = RectAllocator.Stretch, bool clip = false)
        {
            this.gui = gui;
            if (gui == null)
                action = ImGuiAction.Build;
            this.defaultAllocator = defaultAllocator;
            this.clip = clip;
            initialPadding = padding;
        }
        
        public readonly GuiBuilder gui;
        public Window window { get; private set; }
        public ImGui parent { get; private set; }
        private bool rebuildRequested = true;
        private float buildWidth;
        public bool mouseCapture { get; set; } = true;
        public bool valid => !disposed && window != null && window.visible;
        private bool disposed;
        public Vector2 contentSize { get; private set; }
        public ImGuiAction action { get; private set; }
        public bool isBuilding => action == ImGuiAction.Build;
        public int actionParameter { get; private set; }
        private long nextRebuildTimer = long.MaxValue;
        public float pixelsPerUnit { get; private set; }

        private float scale = 1f;
        private readonly bool clip;
        private Vector2 _offset;
        private Rect screenRect;
        private Rect localClip;
        
        public Vector2 offset
        {
            get => _offset;
            set
            {
                screenRect -= (_offset - value);
                _offset = value;
                if (mousePresent)
                    MouseMove(InputSystem.Instance.mouseDownButton);
                else Repaint();
            }
        }

        public bool IsRebuildRequired() => rebuildRequested || Ui.time >= nextRebuildTimer;

        public void Rebuild()
        {
            rebuildRequested = true;
            Repaint();
        }

        public void MarkEverythingForRebuild()
        {
            CheckMainThread();
            rebuildRequested = true;
            foreach (var sub in panels)
                sub.data.MarkEverythingForRebuild();
        }

        public void SetNextRebuild(long nextRebuildTime)
        {
            if (nextRebuildTime < nextRebuildTimer)
            {
                CheckMainThread();
                nextRebuildTimer = nextRebuildTime;
                if (window != null)
                    window.SetNextRepaint(nextRebuildTime);
            }
        }
        
        public void Repaint()
        {
            window?.Repaint();
        }
        
        public Vector2 CalculateState(float width, float pixelsPerUnit)
        {
            if (IsRebuildRequired() || buildWidth != width || this.pixelsPerUnit != pixelsPerUnit)
            {
                this.pixelsPerUnit = pixelsPerUnit;
                BuildGui(width);
            }
            return contentSize;
        }
        
        public void Present(Window window, Rect position, Rect screenClip, ImGui parent)
        {
            this.parent = parent;
            if (IsRebuildRequired() || buildWidth != position.Width)
                BuildGui(position.Width);
            InternalPresent(window, position, screenClip);
        }

        private static readonly List<(SDL.SDL_Rect, RectangleBorder)> borders = new List<(SDL.SDL_Rect, RectangleBorder)>();
        internal void InternalPresent(Window window, Rect position, Rect screenClip)
        {
            this.window = window;
            var renderer = window.renderer;
            SDL.SDL_Rect prevClip = default;
            screenRect = position * scale + offset;
            var screenOffset = screenRect.Position;
            if (clip)
                prevClip = window.SetClip(ToSdlRect(screenClip));
            localClip = new Rect(screenClip.Position - screenOffset, screenClip.Size / scale);
            var currentColor = (SchemeColor) (-1);
            borders.Clear();
            for (var i = rects.Count - 1; i >= 0; i--)
            {
                var (rect, border, color) = rects[i];
                if (!rect.IntersectsWith(localClip))
                    continue;
                var sdlRect = ToSdlRect(rect, screenOffset);
                if (border != RectangleBorder.None)
                    borders.Add((sdlRect, border));
                if (color == SchemeColor.None)
                    continue;
                if (color != currentColor)
                {
                    currentColor = color;
                    var sdlColor = currentColor.ToSdlColor();
                    SDL.SDL_SetRenderDrawColor(renderer, sdlColor.r, sdlColor.g, sdlColor.b, sdlColor.a);
                }
                SDL.SDL_RenderFillRect(renderer, ref sdlRect);
            }
            
            foreach (var (pos, icon, color) in icons)
            {
                if (!pos.IntersectsWith(localClip))
                    continue;
                var sdlpos = ToSdlRect(pos, screenOffset);
                window.DrawIcon(sdlpos, icon, color);
            }

            foreach (var (pos, renderable, color) in renderables)
            {
                if (!pos.IntersectsWith(localClip))
                    continue;
                renderable.Render(renderer, ToSdlRect(pos, screenOffset), color.ToSdlColor());
            }

            foreach (var (srect, type) in borders)
                window.DrawBorder(srect, type);

            foreach (var (rect, batch, _) in panels)
            {
                var intersection = Rect.Intersect(rect, localClip);
                if (intersection == default)
                    continue;
                batch.Present(window, rect + screenOffset, intersection + screenOffset, this);
            }

            if (clip)
                window.SetClip(prevClip);
        }
        
        public IPanel HitTest(Vector2 position)
        {
            position = position / scale - offset;
            for (var i = panels.Count - 1; i >= 0; i--)
            {
                var (rect, panel, _) = panels[i];
                if (panel.mouseCapture && rect.Contains(position))
                    return panel.HitTest(position - rect.Position);
            }

            return this;
        }

        public int UnitsToPixels(float units) => (int) MathF.Round(units * pixelsPerUnit);
        public float PixelsToUnits(int pixels) => pixels / pixelsPerUnit;
        public SDL.SDL_Rect ToSdlRect(Rect rect, Vector2 offset = default)
        {
            return new SDL.SDL_Rect
            {
                x = UnitsToPixels(rect.X + offset.X),
                y = UnitsToPixels(rect.Y + offset.Y),
                w = UnitsToPixels(rect.Width),
                h = UnitsToPixels(rect.Height)
            };
        }
        
        private static void CheckMainThread()
        {
            if (!Ui.IsMainThread())
                throw new NotSupportedException("This should be called from the main thread");
        }

        public Vector2 ToWindowPosition(Vector2 localPosition)
        {
            if (window == null)
                return localPosition;
            return screenRect.Position + localPosition * (window.pixelsPerUnit / pixelsPerUnit);
        }

        public Rect TranslateRect(Rect localPosition, ImGui target)
        {
            var topleft = target.FromWindowPosition(ToWindowPosition(localPosition.TopLeft));
            return new Rect(topleft, localPosition.Size * (target.pixelsPerUnit / pixelsPerUnit));
        }

        public Vector2 FromWindowPosition(Vector2 windowPosition)
        {
            if (window == null)
                return windowPosition;
            return (windowPosition - screenRect.Position) * (pixelsPerUnit / window.pixelsPerUnit);
        }
        
        private void ReleaseUnmanagedResources()
        {
            disposed = true;
            textCache.Dispose();
        }
        
        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~ImGui()
        {
            ReleaseUnmanagedResources();
        }

        private void ExportDrawCommandsTo<T>(List<DrawCommand<T>> sourceList, List<DrawCommand<T>> targetList, Rect rect)
        {
            targetList.Clear();
            var delta = rect.Position;
            for (var i = sourceList.Count - 1; i >= 0; i--)
            {
                var elem = sourceList[i];
                if (rect.Contains(elem.rect))
                    targetList.Add(new DrawCommand<T>(elem.rect - delta, elem.data, elem.color));
                else break;
            }
            targetList.Reverse();
            sourceList.RemoveRange(sourceList.Count-targetList.Count, targetList.Count);
        }
        public void ExportDrawCommandsTo(Rect rect, ImGui target)
        {
            ExportDrawCommandsTo(rects, target.rects, rect);
            ExportDrawCommandsTo(icons, target.icons, rect);
            ExportDrawCommandsTo(renderables, target.renderables, rect);
            ExportDrawCommandsTo(panels, target.panels, rect);
            target.contentSize = rect.Size;
        }
    }
}