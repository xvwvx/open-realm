using Godot;

namespace MdxPreview.Tests
{
    /// <summary>
    /// Prints the Skeleton3D bone properties the running Godot version exposes, so the exporter
    /// can build a track path that resolves instead of hard coding a renamed property.
    /// </summary>
    public partial class TrackPathProbe : Node
    {
        public override void _Ready()
        {
            var skeleton = new Skeleton3D { Name = "Skeleton" };
            AddChild(skeleton);
            skeleton.AddBone("b0");

            GD.Print("[trackprobe] bone properties:");
            foreach (Godot.Collections.Dictionary property in skeleton.GetPropertyList())
            {
                string name = property["name"].AsString();
                if (name.StartsWith("bone")) GD.Print($"[trackprobe]   '{name}'");
            }

            GD.Print($"[trackprobe] skeleton node name = '{skeleton.Name}'");
            GD.Print($"[trackprobe] has 'bone_0/pose' = {skeleton.Get("bone_0/pose").VariantType}");
            GD.Print("[trackprobe] done");
            GetTree().Quit(0);
        }
    }
}
