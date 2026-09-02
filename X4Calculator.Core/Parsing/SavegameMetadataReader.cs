using System.IO.Compression;
using System.Xml;

namespace X4Calculator.Core.Parsing;

/// <summary>从存档头部读取不需要扫描 universe 的轻量元数据。</summary>
internal static class SavegameMetadataReader
{
    public static SavegameMetadata? TryRead(string path)
    {
        try
        {
            using var stream = SavegameFile.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore,
                CloseInput = false
            });

            string? saveName = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Name == "save")
                {
                    saveName = reader.GetAttribute("name");
                }
                else if (reader.Name == "player")
                {
                    return new SavegameMetadata(saveName, reader.GetAttribute("location"));
                }
                else if (reader.Name == "universe")
                {
                    return new SavegameMetadata(saveName, null);
                }
            }

            return new SavegameMetadata(saveName, null);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (XmlException)
        {
        }

        return null;
    }
}

internal sealed record SavegameMetadata(string? SaveName, string? PlayerLocationReference);
