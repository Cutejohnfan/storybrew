﻿using System.Collections.Generic;

namespace StorybrewEditor.Storyboarding
{
    public class FrameStats
    {
        public string LastTexture;
        public bool LastBlendingMode;
        public HashSet<string> LoadedPaths = new HashSet<string>();

        public int SpriteCount;
        public int ProlongedSpriteCount;
        public int Batches;

        public int CommandCount;
        public int EffectiveCommandCount;
        public bool IncompatibleCommands;
        public bool OverlappedCommands;
        public HashSet<string> OverlappedScriptNames = new HashSet<string>();
        public HashSet<string> IncompatibleScriptNames = new HashSet<string>();

        public float ScreenFill;
        public ulong GpuPixelsFrame;
        public double GpuMemoryFrameMb => GpuPixelsFrame / 1024.0 / 1024.0 * 4.0;
    }
}
