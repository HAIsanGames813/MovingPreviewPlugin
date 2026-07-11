using System;
using YukkuriMovieMaker.Plugin;

namespace MovingPreviewPlugin
{
    public class MovingPreviewToolPlugin : IToolPlugin
    {
        public string Name => "ムービングプレビュー";
        public string Title => "ムービングプレビュー";
        public Type ViewType => typeof(MovingPreviewControl);
        public Type ViewModelType => typeof(MovingPreviewViewModel);
    }
}