using BrewLib.Graphics;
using BrewLib.Graphics.Cameras;
using BrewLib.Graphics.Renderers;
using BrewLib.Graphics.Textures;
using OpenTK;
using OpenTK.Graphics;
using System;
using System.Diagnostics;
using System.IO;

namespace StorybrewEditor.Storyboarding
{
    public class VideoLayer : IDisposable
    {
        private readonly VideoPreview videoPreview;

        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1.0f;

        public VideoLayer(VideoPreview videoPreview)
        {
            this.videoPreview = videoPreview ?? throw new ArgumentNullException(nameof(videoPreview));
        }

        public void Draw(DrawContext drawContext, Camera camera, Box2 bounds, float opacity, double timeSeconds, Project project)
        {
            if (!Visible || !videoPreview.IsLoaded || !videoPreview.Enabled)
                return;

            var frameIndex = videoPreview.TimeToFrameIndex(timeSeconds);
            if (frameIndex < 0) return;

            var framePath = videoPreview.GetFramePath(timeSeconds);
            if (framePath == null) return;

            Texture2dRegion texture;
            try
            {
                texture = project.TextureContainer.Get(framePath);
            }
            catch (IOException)
            {
                return;
            }
            if (texture == null) return;

            var finalOpacity = opacity * Opacity;
            if (finalOpacity < 0.001f) return;

            var boundsWidth = bounds.Right - bounds.Left;
            var boundsHeight = bounds.Bottom - bounds.Top;

            var videoAspect = (float)texture.Width / texture.Height;
            var boundsAspect = boundsWidth / boundsHeight;

            float scaleX, scaleY;

            if (videoAspect > boundsAspect)
            {
                scaleY = boundsHeight;
                scaleX = boundsHeight * videoAspect;
            }
            else
            {
                scaleX = boundsWidth;
                scaleY = boundsWidth / videoAspect;
            }

            var centerX = bounds.Left + boundsWidth * 0.5f;
            var centerY = bounds.Top + boundsHeight * 0.5f;

            var color = new Color4(1f, 1f, 1f, finalOpacity);

            DrawState.Prepare(drawContext.Get<QuadRenderer>(), camera, EditorOsbSprite.AlphaBlendStates)
                .Draw(texture, centerX, centerY,
                    texture.Width * 0.5f, texture.Height * 0.5f,
                    scaleX / texture.Width, scaleY / texture.Height,
                    0, color);
        }

        #region IDisposable

        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
        }

        #endregion
    }
}