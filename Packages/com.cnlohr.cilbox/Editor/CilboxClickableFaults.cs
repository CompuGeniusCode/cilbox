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

		static void Report( CilboxUnhandledInterpretedException uhe )
		{
			Exception thr = uhe.Throwee as Exception;
			if( thr == null ) return;

			StringBuilder sb = new StringBuilder();
			List<string> frames = uhe.Frames;
			if( frames != null )
			{
				foreach( string f in frames )
				{
					int a = f.IndexOf( '|' );
					int b = f.LastIndexOf( '|' );
					if( a <= 0 || b <= a ) continue;
					string cls = f.Substring( 0, a );
					string method = f.Substring( a + 1, b - a - 1 );
					int pc;
					if( !int.TryParse( f.Substring( b + 1 ), out pc ) ) continue;

					if( sb.Length > 0 ) sb.Append( '\n' );
					string file; int line;
					if( TryResolve( cls, method, pc, out file, out line ) )
						sb.Append( cls ).Append( '.' ).Append( method ).Append( " () (at " ).Append( ToProjectRelative( file ) ).Append( ':' ).Append( line ).Append( ')' );
					else
						sb.Append( cls ).Append( '.' ).Append( method ).Append( " ()" );
				}
			}

			if( sb.Length > 0 )
			{
				if( !sTriedRemoteStack )
				{
					sTriedRemoteStack = true;
					sRemoteStackField = typeof( Exception ).GetField( "_remoteStackTraceString", BindingFlags.NonPublic | BindingFlags.Instance );
				}
				if( sRemoteStackField != null )
					sRemoteStackField.SetValue( thr, sb.ToString() + "\n" );
			}

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
