using lib7Zip;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace libClonezilla.Cache
{
    //Source-generated STJ metadata: reflection-based JsonSerializer is unsupported (and stripped)
    //under PublishTrimmed - the published exe crashed with FileNotFoundException for the
    //System.Text.Json assembly the first time a build containing these serializers was trimmed.
    //IncludeFields matches the old reflection options; the JSON shape is unchanged, so existing
    //Files.json / toplevel.json caches keep loading.
    [JsonSourceGenerationOptions(IncludeFields = true)]
    [JsonSerializable(typeof(List<ArchiveEntry>))]
    public partial class ArchiveEntryJsonContext : JsonSerializerContext
    {
    }
}
