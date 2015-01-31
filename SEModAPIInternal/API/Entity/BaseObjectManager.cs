namespace SEModAPIInternal.API.Entity
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Reflection;
	using System.Runtime.Serialization;
	using System.Xml;
	using Microsoft.Xml.Serialization.GeneratedAssembly;
	using Sandbox.Common.ObjectBuilders;
	using Sandbox.Common.ObjectBuilders.Definitions;
	using Sandbox.Common.ObjectBuilders.Serializer;
	using SEModAPI.API;
	using SEModAPIInternal.API.Common;
	using SEModAPIInternal.API.Entity.Sector.SectorObject.CubeGrid;
	using SEModAPIInternal.API.Utility;
	using SEModAPIInternal.Support;
	using VRage;

	[DataContract]
	[KnownType( typeof( SectorObjectManager ) )]
	[KnownType( typeof( InventoryItemManager ) )]
	[KnownType( typeof( CubeBlockManager ) )]
	public class BaseObjectManager
	{
		public enum InternalBackingType
		{
			Hashset,
			List,
			Dictionary,
		}

		#region "Attributes"

		private FileInfo m_fileInfo;
		private readonly FieldInfo m_definitionsContainerField;
		private Object m_backingObject;
		private string m_backingSourceMethod;
		private InternalBackingType m_backingSourceType;
		private DateTime m_lastLoadTime;
		private double m_refreshInterval;

		private static double m_averageRefreshDataTime;
		private static DateTime m_lastProfilingOutput;
		private static DateTime m_lastInternalProfilingOutput;

		private static int m_staticRefreshCount;
		private static Dictionary<Type, int> m_staticRefreshCountMap;

		protected FastResourceLock m_resourceLock = new FastResourceLock( );
		protected FastResourceLock m_rawDataHashSetResourceLock = new FastResourceLock( );
		protected FastResourceLock m_rawDataListResourceLock = new FastResourceLock( );
		protected FastResourceLock m_rawDataObjectBuilderListResourceLock = new FastResourceLock( );

		//Flags
		private bool m_isMutable = true;

		private bool m_changed = false;
		private bool m_isDynamic = false;

		//Raw data stores
		protected HashSet<Object> m_rawDataHashSet = new HashSet<object>( );

		protected List<Object> m_rawDataList = new List<object>( );
		protected Dictionary<Object, MyObjectBuilder_Base> m_rawDataObjectBuilderList = new Dictionary<object, MyObjectBuilder_Base>( );

		//Clean data stores
		private Dictionary<long, BaseObject> m_definitions = new Dictionary<long, BaseObject>( );

		#endregion "Attributes"

		#region "Constructors and Initializers"

		public BaseObjectManager( )
		{
			m_fileInfo = null;
			m_changed = false;
			m_isMutable = true;

			m_definitionsContainerField = GetMatchingDefinitionsContainerField( );

			m_backingSourceType = InternalBackingType.Hashset;

			m_lastLoadTime = DateTime.Now;

			if ( m_lastProfilingOutput == null )
				m_lastProfilingOutput = DateTime.Now;
			if ( m_lastInternalProfilingOutput == null )
				m_lastInternalProfilingOutput = DateTime.Now;

			if ( m_staticRefreshCountMap == null )
				m_staticRefreshCountMap = new Dictionary<Type, int>( );

			m_refreshInterval = 250;
		}

		public BaseObjectManager( Object backingSource, string backingMethodName, InternalBackingType backingSourceType )
		{
			m_fileInfo = null;
			m_changed = false;
			m_isMutable = true;
			m_isDynamic = true;

			m_definitionsContainerField = GetMatchingDefinitionsContainerField( );

			m_backingObject = backingSource;
			m_backingSourceMethod = backingMethodName;
			m_backingSourceType = backingSourceType;

			m_lastLoadTime = DateTime.Now;

			if ( m_lastProfilingOutput == null )
				m_lastProfilingOutput = DateTime.Now;
			if ( m_lastInternalProfilingOutput == null )
				m_lastInternalProfilingOutput = DateTime.Now;

			if ( m_staticRefreshCountMap == null )
				m_staticRefreshCountMap = new Dictionary<Type, int>( );

			m_refreshInterval = 250;
		}

		public BaseObjectManager( BaseObject[ ] baseDefinitions )
		{
			m_fileInfo = null;
			m_changed = false;
			m_isMutable = true;

			m_definitionsContainerField = GetMatchingDefinitionsContainerField( );

			Load( baseDefinitions );
		}

		public BaseObjectManager( List<BaseObject> baseDefinitions )
		{
			m_fileInfo = null;
			m_changed = false;
			m_isMutable = true;

			m_definitionsContainerField = GetMatchingDefinitionsContainerField( );

			Load( baseDefinitions );
		}

		#endregion "Constructors and Initializers"

		#region "Properties"

		public bool IsMutable
		{
			get { return m_isMutable; }
			set { m_isMutable = value; }
		}

		protected bool Changed
		{
			get
			{
				if ( m_changed ) return true;
				foreach ( BaseObject def in GetInternalData( ).Values )
				{
					if ( def.Changed )
						return true;
				}
				return false;
			}
		}

		public FileInfo FileInfo
		{
			get { return m_fileInfo; }
			set { m_fileInfo = value; }
		}

		public bool IsDynamic
		{
			get { return m_isDynamic; }
			set { m_isDynamic = value; }
		}

		public bool IsResourceLocked
		{
			get { return m_resourceLock.Owned; }
		}

		public bool IsInternalResourceLocked
		{
			get { return ( m_rawDataHashSetResourceLock.Owned || m_rawDataListResourceLock.Owned || m_rawDataObjectBuilderListResourceLock.Owned ); }
		}

		public bool CanRefresh
		{
			get
			{
				if ( !IsDynamic )
					return false;
				if ( !IsMutable )
					return false;
				if ( IsResourceLocked )
					return false;
				if ( IsInternalResourceLocked )
					return false;
				if ( !SandboxGameAssemblyWrapper.Instance.IsGameStarted )
					return false;
				if ( WorldManager.Instance.IsWorldSaving )
					return false;
				if ( WorldManager.Instance.InternalGetResourceLock( ) == null )
					return false;
				if ( WorldManager.Instance.InternalGetResourceLock( ).Owned )
					return false;

				return true;
			}
		}

		public int Count
		{
			get { return m_definitions.Count; }
		}

		#endregion "Properties"

		#region "Methods"

		public void SetBackingProperties( Object backingObject, string backingMethod, InternalBackingType backingType )
		{
			m_isDynamic = true;

			m_backingObject = backingObject;
			m_backingSourceMethod = backingMethod;
			m_backingSourceType = backingType;
		}

		private FieldInfo GetMatchingDefinitionsContainerField( )
		{
			//Find the the matching field in the container
			Type thisType = typeof( MyObjectBuilder_Base[ ] );
			Type defType = typeof( MyObjectBuilder_Definitions );
			FieldInfo matchingField = null;
			foreach ( FieldInfo field in defType.GetFields( ) )
			{
				Type fieldType = field.FieldType;
				if ( thisType.FullName == fieldType.FullName )
				{
					matchingField = field;
					break;
				}
			}

			return matchingField;
		}

		protected virtual bool IsValidEntity( Object entity )
		{
			return true;
		}

		#region "GetDataSource"

		protected Dictionary<long, BaseObject> GetInternalData( )
		{
			return m_definitions;
		}

		protected HashSet<Object> GetBackingDataHashSet( )
		{
			return m_rawDataHashSet;
		}

		protected List<Object> GetBackingDataList( )
		{
			return m_rawDataList;
		}

		protected Dictionary<object, MyObjectBuilder_Base> GetObjectBuilderMap( )
		{
			return m_rawDataObjectBuilderList;
		}

		#endregion "GetDataSource"

		#region "RefreshDataSource"

		public void Refresh( )
		{
			if ( !CanRefresh )
				return;

			TimeSpan timeSinceLastLoad = DateTime.Now - m_lastLoadTime;
			if ( timeSinceLastLoad.TotalMilliseconds < m_refreshInterval )
				return;
			m_lastLoadTime = DateTime.Now;

			//Run the refresh
			Action action = RefreshData;
			SandboxGameAssemblyWrapper.Instance.EnqueueMainGameAction( action );

			//Update the refresh counts
			if ( !m_staticRefreshCountMap.ContainsKey( this.GetType( ) ) )
				m_staticRefreshCountMap.Add( this.GetType( ), 1 );
			else
				m_staticRefreshCountMap[ this.GetType( ) ]++;
			int typeRefreshCount = m_staticRefreshCountMap[ this.GetType( ) ];
			m_staticRefreshCount++;

			//Adjust the refresh interval based on percentage of total refreshes for this type
			m_refreshInterval = ( typeRefreshCount / m_staticRefreshCount ) * 850 + 250;
		}

		private void RefreshData( )
		{
			if ( !CanRefresh )
				return;

			try
			{
				DateTime startRefreshTime = DateTime.Now;

				if ( m_backingSourceType == InternalBackingType.Hashset )
					InternalRefreshBackingDataHashSet( );
				if ( m_backingSourceType == InternalBackingType.List )
					InternalRefreshBackingDataList( );

				//Lock the main data
				m_resourceLock.AcquireExclusive( );

				//Lock all of the raw data
				if ( m_backingSourceType == InternalBackingType.Hashset )
					m_rawDataHashSetResourceLock.AcquireShared( );
				if ( m_backingSourceType == InternalBackingType.List )
					m_rawDataListResourceLock.AcquireShared( );
				m_rawDataObjectBuilderListResourceLock.AcquireShared( );

				//Refresh the main data
				LoadDynamic( );

				//Unlock the main data
				m_resourceLock.ReleaseExclusive( );

				//Unlock all of the raw data
				if ( m_backingSourceType == InternalBackingType.Hashset )
					m_rawDataHashSetResourceLock.ReleaseShared( );
				if ( m_backingSourceType == InternalBackingType.List )
					m_rawDataListResourceLock.ReleaseShared( );
				m_rawDataObjectBuilderListResourceLock.ReleaseShared( );

				if ( SandboxGameAssemblyWrapper.IsDebugging )
				{
					TimeSpan timeToRefresh = DateTime.Now - startRefreshTime;
					m_averageRefreshDataTime = ( m_averageRefreshDataTime + timeToRefresh.TotalMilliseconds ) / 2;
					TimeSpan timeSinceLastProfilingOutput = DateTime.Now - m_lastProfilingOutput;
					if ( timeSinceLastProfilingOutput.TotalSeconds > 30 )
					{
						m_lastProfilingOutput = DateTime.Now;
						LogManager.APILog.WriteLine( string.Format( "ObjectManager - Average of {0}ms to refresh API data", Math.Round( m_averageRefreshDataTime, 2 ).ToString( ) ) );
					}
				}
			}
			catch ( Exception ex )
			{
				LogManager.ErrorLog.WriteLine( ex );
			}
		}

		protected virtual void LoadDynamic( )
		{
			return;
		}

		protected virtual void InternalRefreshBackingDataHashSet( )
		{
			try
			{
				if ( !CanRefresh )
					return;

				m_rawDataHashSetResourceLock.AcquireExclusive( );

				if ( m_backingObject == null )
					return;
				object rawValue = BaseObject.InvokeEntityMethod( m_backingObject, m_backingSourceMethod );
				if ( rawValue == null )
					return;

				//Create/Clear the hash set
				if ( m_rawDataHashSet == null )
					m_rawDataHashSet = new HashSet<object>( );
				else
					m_rawDataHashSet.Clear( );

				//Only allow valid entities in the hash set
				foreach ( object entry in UtilityFunctions.ConvertHashSet( rawValue ) )
				{
					if ( !IsValidEntity( entry ) )
						continue;

					m_rawDataHashSet.Add( entry );
				}

				m_rawDataHashSetResourceLock.ReleaseExclusive( );
			}
			catch ( Exception ex )
			{
				LogManager.ErrorLog.WriteLine( ex );
				if ( m_rawDataHashSetResourceLock.Owned )
					m_rawDataHashSetResourceLock.ReleaseExclusive( );
			}
		}

		protected virtual void InternalRefreshBackingDataList( )
		{
			try
			{
				if ( !CanRefresh )
					return;

				m_rawDataListResourceLock.AcquireExclusive( );

				if ( m_backingObject == null )
					return;
				object rawValue = BaseObject.InvokeEntityMethod( m_backingObject, m_backingSourceMethod );
				if ( rawValue == null )
					return;

				//Create/Clear the list
				if ( m_rawDataList == null )
					m_rawDataList = new List<object>( );
				else
					m_rawDataList.Clear( );

				//Only allow valid entities in the list
				foreach ( object entry in UtilityFunctions.ConvertList( rawValue ) )
				{
					if ( !IsValidEntity( entry ) )
						continue;

					m_rawDataList.Add( entry );
				}

				m_rawDataListResourceLock.ReleaseExclusive( );
			}
			catch ( Exception ex )
			{
				LogManager.ErrorLog.WriteLine( ex );
				if ( m_rawDataListResourceLock.Owned )
					m_rawDataListResourceLock.ReleaseExclusive( );
			}
		}

		#endregion "RefreshDataSource"

		#region "Static"

		public static FileInfo GetContentDataFile( string configName )
		{
			string filePath = Path.Combine( Path.Combine( GameInstallationInfo.GamePath, @"Content\Data" ), configName );
			FileInfo saveFileInfo = new FileInfo( filePath );

			return saveFileInfo;
		}

		#endregion "Static"

		#region "Serializers"

		public static T LoadContentFile<T, TS>( FileInfo fileInfo ) where TS : XmlSerializer1
		{
			object fileContent = null;

			string filePath = fileInfo.FullName;

			if ( !File.Exists( filePath ) )
			{
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.ConfigFileMissing, filePath );
			}

			try
			{
				fileContent = ReadSpaceEngineersFile<T, TS>( filePath );
			}
			catch ( Exception ex )
			{
				LogManager.ErrorLog.WriteLine( ex );
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.ConfigFileCorrupted, filePath );
			}

			if ( fileContent == null )
			{
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.ConfigFileEmpty, filePath );
			}

			// TODO: set a file watch to reload the files, incase modding is occuring at the same time this is open.
			//     Lock the load during this time, in case it happens multiple times.
			// Report a friendly error if this load fails.

			return (T)fileContent;
		}

		public static void SaveContentFile<T, TS>( T fileContent, FileInfo fileInfo ) where TS : XmlSerializer1
		{
			string filePath = fileInfo.FullName;

			//if (!File.Exists(filePath))
			//{
			//	throw new GameInstallationInfoException(GameInstallationInfoExceptionState.ConfigFileMissing, filePath);
			//}

			try
			{
				WriteSpaceEngineersFile<T, TS>( fileContent, filePath );
			}
			catch
			{
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.ConfigFileCorrupted, filePath );
			}

			if ( fileContent == null )
			{
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.ConfigFileEmpty, filePath );
			}

			// TODO: set a file watch to reload the files, incase modding is occuring at the same time this is open.
			//     Lock the load during this time, in case it happens multiple times.
			// Report a friendly error if this load fails.
		}

		public static T ReadSpaceEngineersFile<T, TS>( string filename )
			where TS : XmlSerializer1
		{
			XmlReaderSettings settings = new XmlReaderSettings
			                             {
				                             IgnoreComments = true,
				                             IgnoreWhitespace = true,
			                             };

			object obj = null;

			if ( File.Exists( filename ) )
			{
				using ( XmlReader xmlReader = XmlReader.Create( filename, settings ) )
				{
					TS serializer = (TS)Activator.CreateInstance( typeof( TS ) );
					obj = serializer.Deserialize( xmlReader );
				}
			}

			return (T)obj;
		}

		protected T Deserialize<T>( string xml )
		{
			using ( StringReader textReader = new StringReader( xml ) )
			{
				return (T)( new XmlSerializerContract( ).GetSerializer( typeof( T ) ).Deserialize( textReader ) );
			}
		}

		protected string Serialize<T>( object item )
		{
			using ( StringWriter textWriter = new StringWriter( ) )
			{
				new XmlSerializerContract( ).GetSerializer( typeof( T ) ).Serialize( textWriter, item );
				return textWriter.ToString( );
			}
		}

		public static bool WriteSpaceEngineersFile<T, TS>( T sector, string filename )
			where TS : XmlSerializer1
		{
			// How they appear to be writing the files currently.
			try
			{
				using ( XmlTextWriter xmlTextWriter = new XmlTextWriter( filename, null ) )
				{
					xmlTextWriter.Formatting = Formatting.Indented;
					xmlTextWriter.Indentation = 2;
					xmlTextWriter.IndentChar = ' ';
					TS serializer = (TS)Activator.CreateInstance( typeof( TS ) );
					serializer.Serialize( xmlTextWriter, sector );
				}
			}
			catch
			{
				return false;
			}

			//// How they should be doing it to support Unicode.
			//var settingsDestination = new XmlWriterSettings()
			//{
			//    Indent = true, // Set indent to false to compress.
			//    Encoding = new UTF8Encoding(false)   // codepage 65001 without signature. Removes the Byte Order Mark from the start of the file.
			//};

			//try
			//{
			//    using (var xmlWriter = XmlWriter.Create(filename, settingsDestination))
			//    {
			//        S serializer = (S)Activator.CreateInstance(typeof(S));
			//        serializer.Serialize(xmlWriter, sector);
			//    }
			//}
			//catch (Exception ex)
			//{
			//    return false;
			//}

			return true;
		}

		#endregion "Serializers"

		#region "GetContent"

		public BaseObject GetEntry( long key )
		{
			if ( !GetInternalData( ).ContainsKey( key ) )
				return null;

			return GetInternalData( )[ key ];
		}

		public List<T> GetTypedInternalData<T>( ) where T : BaseObject
		{
			try
			{
				m_resourceLock.AcquireShared( );

				List<T> newList = new List<T>( );
				foreach ( BaseObject def in GetInternalData( ).Values )
				{
					if ( !( def is T ) )
						continue;

					newList.Add( (T)def );
				}

				m_resourceLock.ReleaseShared( );

				Refresh( );

				return newList;
			}
			catch ( Exception ex )
			{
				LogManager.ErrorLog.WriteLine( ex );
				if ( m_resourceLock.Owned )
					m_resourceLock.ReleaseShared( );
				return new List<T>( );
			}
		}

		#endregion "GetContent"

		#region "NewContent"

		public T NewEntry<T>( ) where T : BaseObject
		{
			if ( !IsMutable ) return default( T );
			MyObjectBuilder_Base newBase = MyObjectBuilderSerializer.CreateNewObject( typeof( MyObjectBuilder_EntityBase ) );
			T newEntry = (T)Activator.CreateInstance( typeof( T ), new object[ ] { newBase } );
			GetInternalData( ).Add( m_definitions.Count, newEntry );
			m_changed = true;

			return newEntry;
		}

		[Obsolete]
		public T NewEntry<T>( MyObjectBuilder_Base source ) where T : BaseObject
		{
			if ( !IsMutable ) return default( T );

			T newEntry = (T)Activator.CreateInstance( typeof( T ), new object[ ] { source } );
			GetInternalData( ).Add( m_definitions.Count, newEntry );
			m_changed = true;

			return newEntry;
		}

		public T NewEntry<T>( T source ) where T : BaseObject
		{
			if ( !IsMutable ) return default( T );

			T newEntry = (T)Activator.CreateInstance( typeof( T ), new object[ ] { source.ObjectBuilder } );
			GetInternalData( ).Add( m_definitions.Count, newEntry );
			m_changed = true;

			return newEntry;
		}

		public void AddEntry<T>( long key, T entry ) where T : BaseObject
		{
			if ( !IsMutable ) return;

			GetInternalData( ).Add( key, entry );
			m_changed = true;
		}

		#endregion "NewContent"

		#region "DeleteContent"

		public bool DeleteEntry( long id )
		{
			if ( !IsMutable ) return false;

			if ( GetInternalData( ).ContainsKey( id ) )
			{
				BaseObject entry = GetInternalData( )[ id ];
				GetInternalData( ).Remove( id );
				entry.Dispose( );
				m_changed = true;
				return true;
			}

			return false;
		}

		public bool DeleteEntry( BaseObject entry )
		{
			if ( !IsMutable ) return false;

			foreach ( KeyValuePair<long, BaseObject> def in m_definitions )
			{
				if ( def.Value.Equals( entry ) )
				{
					DeleteEntry( def.Key );
					break;
				}
			}

			return false;
		}

		public bool DeleteEntries<T>( List<T> entries ) where T : BaseObject
		{
			if ( !IsMutable ) return false;

			foreach ( T entry in entries )
			{
				DeleteEntry( entry );
			}

			return true;
		}

		public bool DeleteEntries<T>( Dictionary<long, T> entries ) where T : BaseObject
		{
			if ( !IsMutable ) return false;

			foreach ( long entry in entries.Keys )
			{
				DeleteEntry( entry );
			}

			return true;
		}

		#endregion "DeleteContent"

		#region "LoadContent"

		public void Load<T>( T[ ] source ) where T : BaseObject
		{
			//Copy the data into the manager
			GetInternalData( ).Clear( );
			foreach ( T definition in source )
			{
				GetInternalData( ).Add( GetInternalData( ).Count, definition );
			}
		}

		public void Load<T>( List<T> source ) where T : BaseObject
		{
			Load( source.ToArray( ) );
		}

		#endregion "LoadContent"

		#region "SaveContent"

		public bool Save( )
		{
			if ( !this.Changed ) return false;
			if ( !this.IsMutable ) return false;
			if ( this.FileInfo == null ) return false;

			MyObjectBuilder_Definitions definitionsContainer = new MyObjectBuilder_Definitions( );

			if ( m_definitionsContainerField == null )
				throw new GameInstallationInfoException( GameInstallationInfoExceptionState.Invalid, "Failed to find matching definitions field in the given file." );

			List<MyObjectBuilder_Base> baseDefs = new List<MyObjectBuilder_Base>( );
			foreach ( BaseObject baseObject in GetInternalData( ).Values )
			{
				baseDefs.Add( baseObject.ObjectBuilder );
			}

			//Save the source data into the definitions container
			m_definitionsContainerField.SetValue( definitionsContainer, baseDefs.ToArray( ) );

			//Save the definitions container out to the file
			SaveContentFile<MyObjectBuilder_Definitions, MyObjectBuilder_DefinitionsSerializer>( definitionsContainer, m_fileInfo );

			return true;
		}

		#endregion "SaveContent"

		#endregion "Methods"
	}
}