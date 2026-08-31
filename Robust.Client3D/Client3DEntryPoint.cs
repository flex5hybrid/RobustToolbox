using System;
using System.Linq;

namespace Robust.Client3D;

internal static class Client3DEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        return args.Any(static argument =>
                string.Equals(argument, "--asset-preview", StringComparison.OrdinalIgnoreCase))
            ? AssetPreviewProgram.Main(args)
            : NetworkedProgram.Main(args);
    }
}
