namespace CanEmAllCoop.Core;

public sealed class Peer
{
    public ulong Id;
    public string Name;
    public bool IsHost;
    public int Index;

    public override string ToString() => $"{Name}#{Index}({Id})";
}
