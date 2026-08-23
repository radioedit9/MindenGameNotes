using System.Buffers.Binary;
using System.Text;

namespace MindenGameNotes;

public sealed class TrueTypeFont
{
    private readonly byte[] data;
    private readonly Dictionary<string,(int Offset,int Length)> tables = [];
    private readonly ushort unitsPerEm, numberOfHMetrics;
    private readonly ushort[] advances;
    private readonly Func<int,int> glyphFor;
    public string Path { get; }
    public string FamilyName { get; }
    public string PostScriptName { get; }
    public ushort EmbeddingFlags { get; }
    public string EmbeddingPermission => (EmbeddingFlags & 0x0002) != 0 ? "Restricted — embedding prohibited" : (EmbeddingFlags & 0x0008) != 0 ? "Editable embedding" : (EmbeddingFlags & 0x0004) != 0 ? "Print/preview embedding" : "Installable embedding";
    public int Ascender { get; }
    public int Descender { get; }
    public (int XMin,int YMin,int XMax,int YMax) Bounds { get; }
    public byte[] Bytes => data;

    public TrueTypeFont(string path)
    {
        Path=path;data=File.ReadAllBytes(path);var count=U16(4);
        for(var i=0;i<count;i++){var o=12+i*16;var tag=Encoding.ASCII.GetString(data,o,4);tables[tag]=((int)U32(o+8),(int)U32(o+12));}
        if(!tables.ContainsKey("glyf") || !tables.ContainsKey("cmap"))throw new NotSupportedException($"{System.IO.Path.GetFileName(path)} is not a conventional TrueType glyf font.");
        var head=Table("head");unitsPerEm=U16(head+18);Bounds=(I16(head+36),I16(head+38),I16(head+40),I16(head+42));
        var hhea=Table("hhea");Ascender=Scale(I16(hhea+4));Descender=Scale(I16(hhea+6));numberOfHMetrics=U16(hhea+34);
        var maxp=Table("maxp");var glyphs=U16(maxp+4);advances=new ushort[glyphs];var hmtx=Table("hmtx");ushort last=0;for(var i=0;i<glyphs;i++){if(i<numberOfHMetrics)last=U16(hmtx+i*4);advances[i]=last;}
        var os2=Table("OS/2");EmbeddingFlags=U16(os2+8);FamilyName=Name(1)??System.IO.Path.GetFileNameWithoutExtension(path);PostScriptName=(Name(6)??FamilyName).Replace(" ","");glyphFor=BuildCmap();
    }

    public int Width(int unicode){var g=glyphFor(unicode);return g>=0&&g<advances.Length?(int)Math.Round(advances[g]*1000d/unitsPerEm):500;}
    public string WidthArray(){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var enc=Encoding.GetEncoding(1252);return string.Join(' ',Enumerable.Range(32,224).Select(b=>Width(enc.GetChars([(byte)b])[0])));}
    public string FontBBox=>$"[{Scale(Bounds.XMin)} {Scale(Bounds.YMin)} {Scale(Bounds.XMax)} {Scale(Bounds.YMax)}]";
    private int Scale(int v)=>(int)Math.Round(v*1000d/unitsPerEm);
    private int Table(string tag)=>tables.TryGetValue(tag,out var t)?t.Offset:throw new InvalidDataException($"Font table {tag} is missing.");
    private string? Name(int id){if(!tables.TryGetValue("name",out var t))return null;var count=U16(t.Offset+2);var storage=t.Offset+U16(t.Offset+4);for(var i=0;i<count;i++){var o=t.Offset+6+i*12;if(U16(o+6)!=id)continue;var platform=U16(o);var len=U16(o+8);var pos=storage+U16(o+10);return platform is 0 or 3?Encoding.BigEndianUnicode.GetString(data,pos,len):Encoding.Latin1.GetString(data,pos,len);}return null;}
    private Func<int,int> BuildCmap(){var c=Table("cmap");var n=U16(c+2);int chosen=0;for(var i=0;i<n;i++){var o=c+4+i*8;var platform=U16(o);var encoding=U16(o+2);var sub=c+(int)U32(o+4);if(U16(sub)==4&&(platform==3&&(encoding==1||encoding==10)||platform==0)){chosen=sub;break;}}if(chosen==0)throw new InvalidDataException("Font has no supported Unicode cmap.");var segCount=U16(chosen+6)/2;var end=chosen+14;var start=end+segCount*2+2;var delta=start+segCount*2;var range=delta+segCount*2;return cp=>{for(var i=0;i<segCount;i++){var en=U16(end+i*2);if(cp>en)continue;var st=U16(start+i*2);if(cp<st)return 0;var d=I16(delta+i*2);var ro=U16(range+i*2);if(ro==0)return(cp+d)&0xffff;var addr=range+i*2+ro+(cp-st)*2;if(addr+1>=data.Length)return 0;var g=U16(addr);return g==0?0:(g+d)&0xffff;}return 0;};}
    private ushort U16(int o)=>BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o,2));private short I16(int o)=>BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o,2));private uint U32(int o)=>BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o,4));
}

public sealed record TypographyVariant(string Id,string DisplayName,string DisplayPath,string BodyName,string BodyPath)
{
    public static IReadOnlyList<TypographyVariant> Candidates =>
    [
        new("impact-arialnarrow","Impact","C:\\Windows\\Fonts\\impact.ttf","Arial Narrow","C:\\Windows\\Fonts\\Arialn.ttf")
    ];
}
