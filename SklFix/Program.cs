using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
using SklFix.Util;
using LeagueToolkit.IO.WadFile;
using LeagueToolkit.IO.SkeletonFile;
using LeagueToolkit.Helpers;
using ZstdSharp;
using System.Diagnostics;

namespace SklFix
{
    class Program
    {

        private static readonly List<string> files = new();
        private static readonly string directory = Path.GetDirectoryName( Process.GetCurrentProcess().MainModule.FileName );

        static void Main( string[] args )
        {
            Dictionary<string, string> hashes = null;
            try
            {
                using MemoryStream stream = CreateMemoryStream( File.OpenRead(
                    Path.Combine( directory, "hashes_game.json" ) ) ); 
                hashes = JsonSerializer.Deserialize<Dictionary<string, string>>( stream.ToArray() );
            } catch( Exception ex )
            {
                Console.WriteLine( "The file hashes_game.json does not exist." );
                Console.WriteLine( ex );
                PressAnyKey();
                return;
            }
            if ( args.Length == 0 )
            {
                Console.WriteLine( "You did not drop anything." );
                PressAnyKey();
                return;
            }
            foreach ( var file in args )
                RetrieveFiles( file );
            if ( files.Count == 0 ) return;
            Regex regex = new Regex( "^ASSETS/Characters/[a-zA-Z]+/", RegexOptions.IgnoreCase );
            foreach ( var fileLocation in files )
            {
                using ( FileStream stream = new FileStream( fileLocation, FileMode.Open ) )
                {
                    using ( ZipArchive zip = new ZipArchive( stream, ZipArchiveMode.Read ) )
                    {
                        ZipArchiveEntry[] metaEntries = GetEntries( zip.Entries, @"^META[\\/].*" ).ToArray();
                        string fileName = Path.GetFileNameWithoutExtension( fileLocation );
                        if ( metaEntries.Length == 0 )
                        {
                            Console.WriteLine( "The zip " + fileName + " does not have META folder." );
                            continue;
                        }

                        ModInfo info = null;

                        foreach ( var entryMeta in metaEntries )
                        {
                            if ( entryMeta.Name.ToLower() != "info.json" ) continue;
                            using MemoryStream memoryStream = CreateMemoryStream( entryMeta.Open() );
                            try
                            {
                                info = JsonSerializer.Deserialize<ModInfo>( memoryStream.ToArray() );
                            } catch ( Exception ex )
                            {
                                Console.WriteLine( "Something is wrong in the file 'info.json' - zip file: " + fileName );
                            }
                            break;
                        }

                        if ( info == null ) continue;

                        ZipArchiveEntry[] wadEntries = GetEntries( zip.Entries, @"^WAD[\\/].+" ).ToArray();
                        ZipArchiveEntry[] rawEntries = GetEntries( zip.Entries, @"^RAW[\\/].+" ).ToArray();

                        if ( wadEntries.Length == 0 && rawEntries.Length == 0 )
                        {
                            Console.WriteLine( "The zip " + fileName + " has neither wad folder nor raw folder." );
                            continue;
                        }
                        WadBuilder wb = null;
                        string wadName = null;
                        if ( rawEntries.Length > 0 )
                        {
                            if ( !HasSklRawFolder( rawEntries ) )
                            {
                                Console.WriteLine( "The zip " + fileName + " has no skl file." );
                                continue;
                            }
                            wb = new WadBuilder();
                            foreach ( var entry in rawEntries )
                            {
                                string extension = Path.GetExtension( entry.Name );
                                WadEntryBuilder builder = new WadEntryBuilder();
                                WadEntryType type = Utilities.GetExtensionWadCompressionType( extension );
                                string path = entry.FullName.Replace( "\\", "/" ).Replace( "RAW/", "" );
                                if ( wadName == null )
                                {
                                    Match match = regex.Match( path );
                                    if ( match.Success )
                                    {
                                        string x = match.Value;
                                        x = x.Remove( x.LastIndexOf( '/' ) );
                                        wadName = x.Substring( x.LastIndexOf( '/' ) + 1 );
                                    }

                                }
                                builder.WithPath( path );
                                switch ( extension )
                                {
                                    case ".skl":
                                        using ( Stream zipStream = entry.Open() )
                                        {
                                            UpdateSkl( zipStream, builder );
                                        }
                                        break;
                                    default:
                                        using ( Stream zipStream = entry.Open() )
                                        {
                                            switch ( type )
                                            {

                                                case WadEntryType.Uncompressed:
                                                    builder.WithUncompressedDataStream(
                                                        CreateMemoryStream( zipStream ) );
                                                    break;
                                                case WadEntryType.ZStandardCompressed:
                                                    Stream compressedStream = StreamToZStandardCompress( zipStream );
                                                    builder.WithZstdDataStream( compressedStream,
                                                        ( int ) compressedStream.Length, ( int ) entry.Length );
                                                    break;
                                                default:
                                                    break;
                                            }
                                        }
                                        break;
                                }
                                wb.WithEntry( builder );
                            }
                        } else
                        {
                            foreach ( var zipEntry in wadEntries )
                            {
                                string name = zipEntry.Name;
                                if ( !name.Contains( ".wad.client" ) ) continue;
                                using Stream zipStream = zipEntry.Open();
                                Wad wad = Wad.Mount( CreateMemoryStream( zipStream ), false );

                                if ( wb == null )
                                    if ( !HasSklWadFolder( wad, hashes ) )
                                        continue;
                                    else wb = new WadBuilder();

                                foreach ( var entry in wad.Entries.Values )
                                {
                                    WadEntryBuilder builder = new WadEntryBuilder();
                                    builder.WithPathXXHash( entry.XXHash );
                                    string hash = entry.XXHash.ToString( "x16" );
                                    if ( hashes.ContainsKey( hash ) )
                                    {
                                        if ( string.IsNullOrEmpty( wadName ) )
                                            wadName = name;
                                        using Stream stm = entry.GetDataHandle().GetDecompressedStream();
                                        UpdateSkl( stm, builder );
                                    } else
                                    {
                                        switch ( entry.Type )
                                        {

                                            case WadEntryType.ZStandardCompressed:
                                                builder.WithZstdDataStream(
                                                     entry.GetDataHandle().GetCompressedStream(),
                                                     entry.CompressedSize,
                                                     entry.UncompressedSize );
                                                break;

                                            case WadEntryType.Uncompressed:
                                                builder.WithUncompressedDataStream(
                                                     entry.GetDataHandle().GetDecompressedStream() );
                                                break;

                                        }
                                    }
                                    wb.WithEntry( builder );
                                }
                            }
                        }
                        if ( wb != null )
                        {
                            if ( wadName == null ) continue;
                            //info.json
                            info.Description = "Updated the model to the latest version";
                            info.Version = ( ( float ) ( float.Parse( info.Version ) + 0.1 ) ).ToString( "0.0" );
                            //Create file
                            string filesDirectory = Path.Combine( directory, "files_updated" );
                            Directory.CreateDirectory( filesDirectory );
                            try
                            {
                                using ( FileStream fileStream = new FileStream( Path.Combine( filesDirectory, GetZipName( info ) ), FileMode.Create ) )
                                {
                                    using ( ZipArchive archive = new ZipArchive( fileStream, ZipArchiveMode.Update ) )
                                    {
                                        var entry = archive.CreateEntry( @"META\info.json" );
                                        using ( StreamWriter writer = new StreamWriter( entry.Open() ) )
                                        {
                                            writer.WriteLine( JsonSerializer.Serialize( info ) );
                                        }
                                        entry = archive.CreateEntry( @"WAD\" +
                                            ( wadName.Contains( ".wad.client" ) ? wadName : wadName + ".wad.client" ) );
                                        using ( Stream s = entry.Open() )
                                        {
                                            wb.Build( s, false );
                                        }
                                        var image = GetImage( metaEntries );
                                        if ( image != null )
                                        {
                                            entry = archive.CreateEntry( @"META\image.png" );
                                            using ( Stream s = entry.Open() )
                                            {
                                                using ( Stream imageStream = image.Open() )
                                                {
                                                    imageStream.CopyTo( s );
                                                }
                                            }
                                        }
                                        Console.WriteLine( "Zip " + fileName + " updated." );
                                    }
                                }
                            } catch ( Exception ex )
                            {
                                Console.WriteLine( "Error in zip: " + fileName );
                                Console.WriteLine( ex );
                            }
                        } else 
                            Console.WriteLine( "The zip " + fileName + " could not be updated." );
                    }

                }
            }
            PressAnyKey();
        }

        private static void RetrieveFiles( string location )
        {
            if ( IsDirectory( location ) )
                foreach( var file in GetAllFiles( location ) )
                    RetrieveFiles( file );
            else
                if ( IsFantomeFile( location ) )
                    files.Add( location );  
        }

        private static List<string> GetAllFiles( string d )
        {
            List<string> list = new();
            foreach ( var file in Directory.GetFiles( d ) )
                list.Add( file );
            foreach ( var file in Directory.GetDirectories( d ) )
                list.Add( file );
            return list;
        }

        private static bool IsDirectory( string location )
        {
            return File.GetAttributes( location ).HasFlag( FileAttributes.Directory );
        }

        private static void UpdateSkl( Stream file, WadEntryBuilder builder )
        {
            using ( FileStream openWrite = File.OpenWrite( "DONT_DELETE" ) )
            {
                Skeleton skl = new Skeleton( CreateMemoryStream( file ) );
                skl.Write( openWrite );
            }
            using ( FileStream fileStream = File.OpenRead( "DONT_DELETE" ) )
            {
                Stream compressedStream = StreamToZStandardCompress( fileStream );
                builder.WithZstdDataStream( compressedStream, ( int ) compressedStream.Length, ( int ) fileStream.Length );
            }
            File.Delete( "DONT_DELETE" );
        }

        private static ZipArchiveEntry GetImage( ZipArchiveEntry[] entries )
        {
            return entries.Where( x => x.Name == "image.png" ).FirstOrDefault();
        }

        private static string GetZipName( ModInfo modInfo )
        {
            return new StringBuilder
                ( modInfo.Name.Trim() )
                .Append( " - " )
                .Append( modInfo.Version )
                .Append( " (By " )
                .Append( string.IsNullOrEmpty( modInfo.Author ) ? "Unknown" : modInfo.Author.Trim() )
                .Append( ").zip" )
                .ToString();
        }

        private static MemoryStream StreamToZStandardCompress( Stream stream )
        {
            MemoryStream compressedStream = new MemoryStream();
            using ( ZstdStream zstdStream = new ZstdStream( compressedStream, ZstdStreamMode.Compress, true ) )
            {
                stream.CopyTo( zstdStream );
            }
            return compressedStream;
        }

        private static MemoryStream CreateMemoryStream( Stream stream )
        {
            MemoryStream memoryStream = new MemoryStream();
            stream.CopyTo( memoryStream );
            memoryStream.Position = 0;
            return memoryStream;
        }

        //https://github.com/LoL-Fantome/Fantome/blob/c45e3d1817afa15ab6e72f414c67ed04bdf918a1/Fantome/ModManagement/IO/ModFile.cs#L160
        public static IEnumerable<ZipArchiveEntry> GetEntries( ICollection<ZipArchiveEntry> entries, string regexPattern )
        {
            return entries.Where( x => Regex.IsMatch( x.FullName, regexPattern ) );
        }

        private static bool IsFantomeFile( string fileLocation )
        {
            string extension = Path.GetExtension( fileLocation );
            return extension == ".zip" || extension == ".fantome";
        }

        private static bool HasSklRawFolder( ZipArchiveEntry[] rawEntries )
        {
            foreach( var entry in rawEntries )
            {
                string extension = Path.GetExtension( entry.Name );
                if ( string.IsNullOrEmpty( extension ) ) continue;
                if ( extension == ".skl" )
                    return true;
            }
            return false;
        }

        private static bool HasSklWadFolder( Wad wad, Dictionary<string, string> hashes )
        {
            foreach ( var entry in wad.Entries.Keys )
            {
                string hash = entry.ToString( "x16" );
                if ( hashes.ContainsKey( hash ) )
                    return true;
            }
            return false;
        }
        
        private static void PressAnyKey()
        {
            Console.WriteLine( "Press any key to exit..." );
            Console.ReadKey();
        }

    }
}
