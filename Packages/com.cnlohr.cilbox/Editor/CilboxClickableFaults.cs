#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Cilbox
{
	// NexVR (editor-only): renders an unhandled interpreted fault as a genuine Unity exception whose stack
	// frames are individually clickable to the world .cs. The interpreter's pc IS the raw IL offset, so each
	// world frame (deepest first, accumulated as the throw unwinds) is mapped to a source line via the
	// assembly's PDB sequence points (read through Mono.Cecil by reflection, so the asmdef carries no Cecil
	// dependency) and formatted as Unity's own "Class.Method () (at Assets/..File.cs:line)" frame. We inject
	// that into the real throwee's stack trace (Exception._remoteStackTraceString) and log it with
	// Debug.LogException, so Unity treats it as a real exception: clean header (the real exception type),
	// per-frame-clickable frames, double-click opens the throw. (Unity appends the interpreter's own C#
	// frames below, like the UnityEngine.* engine frames under any normal exception.)
	[InitializeOnLoad]
	internal static class CilboxClickableFaults
	{
		static CilboxClickableFaults()
		{
			CilboxFault.InterpretedReporter = Report;
		}

		class SeqInfo { public string file; public int[] offs; public int[] lines; }
		static readonly Dictionary<string, SeqInfo> sCache = new Dictionary<string, SeqInfo>();
		static FieldInfo sRemoteStackField;
		static bool sTriedRemoteStack;

		internal struct WorldFrame
		{
			public string cls;
			public string method;
			public int pc;
			public WorldFrame( string cls, string method, int pc ) { this.cls = cls; this.method = method; this.pc = pc; }
		}

		[Serializable]
		internal sealed class CilboxSourceException : Exception
		{
			public CilboxSourceException( string message ) : base( message ) { }
		}

		static void Report( CilboxUnhandledInterpretedException uhe )
		{
			Exception thr = uhe.Throwee as Exception;
			if( thr == null ) return;

			List<string> lines = BuildWorldFrameLines( ParseFrames( uhe.Frames ) );
			if( lines.Count > 0 )
				SetRemoteStack( thr, string.Join( "\n", lines ) + "\n" );
		}

		internal static void LogSourceException( string message, IList<WorldFrame> frames )
		{
			LogSourceExceptionLines( message, BuildWorldFrameLines( frames ) );
		}

		internal static void LogSourceExceptionLines( string message, IList<string> frameLines, bool suppressCallStack = false )
		{
			CilboxSourceException ex = new CilboxSourceException( message );
			if( frameLines != null && frameLines.Count > 0 )
				SetRemoteStack( ex, string.Join( "\n", frameLines ) + "\n" );

			if( !suppressCallStack )
			{
				Debug.LogException( ex );
				return;
			}

			StackTraceLogType prev = Application.GetStackTraceLogType( LogType.Exception );
			Application.SetStackTraceLogType( LogType.Exception, StackTraceLogType.None );
			try { Debug.LogException( ex ); }
			finally { Application.SetStackTraceLogType( LogType.Exception, prev ); }
		}

		internal static List<string> ResolveFrameLines( IList<WorldFrame> frames )
		{
			return BuildWorldFrameLines( frames );
		}

		internal static string FrameLineForType( string typeFullName, string projectRelativeFile, int line )
		{
			int dot = typeFullName.LastIndexOf( '.' );
			string simple = dot >= 0 ? typeFullName.Substring( dot + 1 ) : typeFullName;
			return typeFullName + "." + simple + " () (at " + projectRelativeFile + ":" + line + ")";
		}

		static List<WorldFrame> ParseFrames( List<string> frames )
		{
			List<WorldFrame> ret = new List<WorldFrame>();
			if( frames == null ) return ret;
			foreach( string f in frames )
			{
				int a = f.IndexOf( '|' );
				int b = f.LastIndexOf( '|' );
				if( a <= 0 || b <= a ) continue;
				string cls = f.Substring( 0, a );
				string method = f.Substring( a + 1, b - a - 1 );
				if( !int.TryParse( f.Substring( b + 1 ), out int pc ) ) continue;
				ret.Add( new WorldFrame( cls, method, pc ) );
			}
			return ret;
		}

		static List<string> BuildWorldFrameLines( IList<WorldFrame> frames )
		{
			List<string> lines = new List<string>();
			if( frames == null ) return lines;
			foreach( WorldFrame f in frames )
			{
				if( TryResolve( f.cls, f.method, f.pc, out string file, out int line ) )
					lines.Add( f.cls + "." + f.method + " () (at " + ToProjectRelative( file ) + ":" + line + ")" );
				else
					lines.Add( f.cls + "." + f.method + " ()" );
			}
			return lines;
		}

		static void SetRemoteStack( Exception ex, string stack )
		{
			if( !sTriedRemoteStack )
			{
				sTriedRemoteStack = true;
				sRemoteStackField = typeof( Exception ).GetField( "_remoteStackTraceString", BindingFlags.NonPublic | BindingFlags.Instance );
			}
			if( sRemoteStackField != null )
				sRemoteStackField.SetValue( ex, stack );
		}

		static string ToProjectRelative( string file )
		{
			string rel = file.Replace( '\\', '/' );
			string dataPath = Application.dataPath.Replace( '\\', '/' );
			if( rel.StartsWith( dataPath ) ) rel = "Assets" + rel.Substring( dataPath.Length );
			return rel;
		}

		static bool TryResolve( string className, string methodName, int pc, out string file, out int line )
		{
			file = null; line = 0;
			if( string.IsNullOrEmpty( className ) || string.IsNullOrEmpty( methodName ) ) return false;
			Type type = FindType( className );
			if( type == null ) return false;
			string dll = type.Assembly.Location;
			if( string.IsNullOrEmpty( dll ) || !File.Exists( dll ) ) return false;

			string key = dll + "|" + className + "|" + methodName;
			SeqInfo info;
			if( !sCache.TryGetValue( key, out info ) )
			{
				info = ReadSeqPoints( dll, className, methodName );
				sCache[key] = info;
			}
			if( info == null || info.offs == null || info.offs.Length == 0 ) return false;

			int bestOff = -1;
			for( int i = 0; i < info.offs.Length; i++ )
				if( info.offs[i] <= pc && info.offs[i] > bestOff ) { bestOff = info.offs[i]; line = info.lines[i]; }
			if( bestOff < 0 ) return false;
			file = info.file;
			return true;
		}

		static Type FindType( string fullName )
		{
			foreach( Assembly a in AppDomain.CurrentDomain.GetAssemblies() )
			{
				try { Type t = a.GetType( fullName ); if( t != null ) return t; } catch { }
			}
			return null;
		}

		// Read (IL offset -> source line) sequence points for one method via Mono.Cecil, reached by reflection
		// so the asmdef needs no Cecil reference (and avoids the Mono.Cecil / Unity.Burst.Cecil ambiguity).
		static SeqInfo ReadSeqPoints( string dll, string className, string methodName )
		{
			string pdb = Path.ChangeExtension( dll, ".pdb" );
			if( !File.Exists( pdb ) ) return null;
			Assembly cecil = null;
			foreach( Assembly a in AppDomain.CurrentDomain.GetAssemblies() )
				if( a.GetType( "Mono.Cecil.AssemblyDefinition" ) != null ) { cecil = a; break; }
			if( cecil == null ) return null;
			try
			{
				Type tAsm = cecil.GetType( "Mono.Cecil.AssemblyDefinition" );
				Type tRP = cecil.GetType( "Mono.Cecil.ReaderParameters" );
				object rp = Activator.CreateInstance( tRP );
				tRP.GetProperty( "ReadSymbols" ).SetValue( rp, true );
				MethodInfo read = tAsm.GetMethod( "ReadAssembly", new Type[] { typeof( string ), tRP } );
				object asm = read.Invoke( null, new object[] { dll, rp } );
				using( (IDisposable)asm )
				{
					object module = tAsm.GetProperty( "MainModule" ).GetValue( asm );
					MethodInfo getType = module.GetType().GetMethod( "GetType", new Type[] { typeof( string ) } );
					object td = getType.Invoke( module, new object[] { className } );
					if( td == null ) return null;
					IEnumerable methods = (IEnumerable)td.GetType().GetProperty( "Methods" ).GetValue( td );
					foreach( object md in methods )
					{
						if( (string)md.GetType().GetProperty( "Name" ).GetValue( md ) != methodName ) continue;
						object di = md.GetType().GetProperty( "DebugInformation" ).GetValue( md );
						if( di == null ) continue;
						if( !(bool)di.GetType().GetProperty( "HasSequencePoints" ).GetValue( di ) ) continue;
						IEnumerable sps = (IEnumerable)di.GetType().GetProperty( "SequencePoints" ).GetValue( di );
						List<int> offs = new List<int>();
						List<int> lines = new List<int>();
						string file = null;
						foreach( object sp in sps )
						{
							if( (bool)sp.GetType().GetProperty( "IsHidden" ).GetValue( sp ) ) continue;
							int off = (int)sp.GetType().GetProperty( "Offset" ).GetValue( sp );
							int ln = (int)sp.GetType().GetProperty( "StartLine" ).GetValue( sp );
							object doc = sp.GetType().GetProperty( "Document" ).GetValue( sp );
							if( file == null ) file = (string)doc.GetType().GetProperty( "Url" ).GetValue( doc );
							offs.Add( off ); lines.Add( ln );
						}
						SeqInfo si = new SeqInfo();
						si.file = file; si.offs = offs.ToArray(); si.lines = lines.ToArray();
						return si;
					}
				}
			}
			catch { }
			return null;
		}
	}
}
#endif
