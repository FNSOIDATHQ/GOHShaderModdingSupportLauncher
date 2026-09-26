namespace GOHShaderModdingSupportLauncher;

public sealed class Mod
{
    public string name { get; set; }
    public string type { get; set; }
    public bool hasShader { get; set; }
    public string path;
    public string folderName;
    public bool hasLoad;

    public Mod(string n, string t, string p, string fn, bool hS)
    {
        name = n;
        type = t;
        path = p;
        folderName = fn;
        hasShader = hS;
    }
}
