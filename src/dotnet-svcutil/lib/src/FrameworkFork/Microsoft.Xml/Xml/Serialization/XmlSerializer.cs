// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.Xml.Serialization
{
    using System.Reflection;
    using System.Collections;
    using System.IO;
    using Microsoft.Xml.Schema;
    using System;
    using System.Text;
    using System.Threading;
    using System.Globalization;
    using System.Security;
    // using System.Security.Permissions;
    // using System.Security.Policy;
    //using Microsoft.Xml.Serialization.Configuration;
    using System.Diagnostics;
    using Microsoft.CodeDom.Compiler;
    using System.Runtime.Versioning;

    /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlDeserializationEvents"]/*' />
    /// <devdoc>
    ///    <para>[To be supplied.]</para>
    /// </devdoc>
    public struct XmlDeserializationEvents
    {
        private XmlNodeEventHandler _onUnknownNode;
        private XmlAttributeEventHandler _onUnknownAttribute;
        private XmlElementEventHandler _onUnknownElement;
        private UnreferencedObjectEventHandler _onUnreferencedObject;
        internal object sender;

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlDeserializationEvents.OnUnknownNode"]/*' />
        public XmlNodeEventHandler OnUnknownNode
        {
            get
            {
                return _onUnknownNode;
            }

            set
            {
                _onUnknownNode = value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlDeserializationEvents.OnUnknownAttribute"]/*' />
        public XmlAttributeEventHandler OnUnknownAttribute
        {
            get
            {
                return _onUnknownAttribute;
            }
            set
            {
                _onUnknownAttribute = value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlDeserializationEvents.OnUnknownElement"]/*' />
        public XmlElementEventHandler OnUnknownElement
        {
            get
            {
                return _onUnknownElement;
            }
            set
            {
                _onUnknownElement = value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlDeserializationEvents.OnUnreferencedObject"]/*' />
        public UnreferencedObjectEventHandler OnUnreferencedObject
        {
            get
            {
                return _onUnreferencedObject;
            }
            set
            {
                _onUnreferencedObject = value;
            }
        }
    }

    /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation"]/*' />
    ///<internalonly/>
    /// <devdoc>
    ///    <para>[To be supplied.]</para>
    /// </devdoc>
    public abstract class XmlSerializerImplementation
    {
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.Reader"]/*' />
        public virtual XmlSerializationReader Reader { get { throw new NotSupportedException(); } }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.Writer"]/*' />
        public virtual XmlSerializationWriter Writer { get { throw new NotSupportedException(); } }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.ReadMethods"]/*' />
        public virtual Hashtable ReadMethods { get { throw new NotSupportedException(); } }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.WriteMethods"]/*' />
        public virtual Hashtable WriteMethods { get { throw new NotSupportedException(); } }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.TypedSerializers"]/*' />
        public virtual Hashtable TypedSerializers { get { throw new NotSupportedException(); } }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.CanSerialize"]/*' />
        public virtual bool CanSerialize(Type type) { throw new NotSupportedException(); }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializerImplementation.GetSerializer"]/*' />
        public virtual XmlSerializer GetSerializer(Type type) { throw new NotSupportedException(); }
    }

    /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer"]/*' />
    /// <devdoc>
    ///    <para>[To be supplied.]</para>
    /// </devdoc>
    public class XmlSerializer
    {
        private TempAssembly _tempAssembly;
        private bool _typedSerializer;
        private Type _primitiveType;
        private XmlMapping _mapping;
        private XmlDeserializationEvents _events = new XmlDeserializationEvents();

        private static TempAssemblyCache s_cache = new TempAssemblyCache();
        private static volatile XmlSerializerNamespaces s_defaultNamespaces;
        private static XmlSerializerNamespaces DefaultNamespaces
        {
            get
            {
                if (s_defaultNamespaces == null)
                {
                    XmlSerializerNamespaces nss = new XmlSerializerNamespaces();
                    nss.AddInternal("xsi", XmlSchema.InstanceNamespace);
                    nss.AddInternal("xsd", XmlSchema.Namespace);
                    if (s_defaultNamespaces == null)
                    {
                        s_defaultNamespaces = nss;
                    }
                }
                return s_defaultNamespaces;
            }
        }

        private static Hashtable s_xmlSerializerTable = new Hashtable();

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer8"]/*' />
        ///<internalonly/>
        protected XmlSerializer()
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type, XmlAttributeOverrides overrides, Type[] extraTypes, XmlRootAttribute root, string defaultNamespace) :
            this(type, overrides, extraTypes, root, defaultNamespace, null)
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer2"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type, XmlRootAttribute root) : this(type, null, new Type[0], root, null, null)
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer3"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type, Type[] extraTypes) : this(type, null, extraTypes, null, null, null)
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer4"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type, XmlAttributeOverrides overrides) : this(type, overrides, new Type[0], null, null, null)
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer5"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(XmlTypeMapping xmlTypeMapping)
        {
            _tempAssembly = GenerateTempAssembly(xmlTypeMapping);
            _mapping = xmlTypeMapping;
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer6"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type) : this(type, (string)null)
        {
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer1"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlSerializer(Type type, string defaultNamespace)
        {
            if (type == null)
                throw new ArgumentNullException("type");
            _mapping = GetKnownMapping(type, defaultNamespace);
            if (_mapping != null)
            {
                _primitiveType = type;
                return;
            }
            _tempAssembly = s_cache[defaultNamespace, type];
            if (_tempAssembly == null)
            {
                lock (s_cache)
                {
                    _tempAssembly = s_cache[defaultNamespace, type];
                    if (_tempAssembly == null)
                    {
                        XmlSerializerImplementation contract = null;
                        Assembly assembly = TempAssembly.LoadGeneratedAssembly(type, defaultNamespace, out contract);
                        if (assembly == null)
                        {
                            // need to reflect and generate new serialization assembly
                            XmlReflectionImporter importer = new XmlReflectionImporter(defaultNamespace);
                            _mapping = importer.ImportTypeMapping(type, null, defaultNamespace);
                            _tempAssembly = GenerateTempAssembly(_mapping, type, defaultNamespace);
                        }
                        else
                        {
                            // we found the pre-generated assembly, now make sure that the assembly has the right serializer
                            // try to avoid the reflection step, need to get ElementName, namespace and the Key form the type
                            _mapping = XmlReflectionImporter.GetTopLevelMapping(type, defaultNamespace);
                            _tempAssembly = new TempAssembly(new XmlMapping[] { _mapping }, assembly, contract);
                        }
                    }
                    s_cache.Add(defaultNamespace, type, _tempAssembly);
                }
            }
            if (_mapping == null)
            {
                _mapping = XmlReflectionImporter.GetTopLevelMapping(type, defaultNamespace);
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.XmlSerializer7"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        internal XmlSerializer(Type type, XmlAttributeOverrides overrides, Type[] extraTypes, XmlRootAttribute root, string defaultNamespace, string location)
        {
            if (type == null)
                throw new ArgumentNullException("type");
            XmlReflectionImporter importer = new XmlReflectionImporter(overrides, defaultNamespace);
            if (extraTypes != null)
            {
                for (int i = 0; i < extraTypes.Length; i++)
                    importer.IncludeType(extraTypes[i]);
            }
            _mapping = importer.ImportTypeMapping(type, root, defaultNamespace);
            if (location != null)
            {
                DemandForUserLocationOrEvidence();
            }
            _tempAssembly = GenerateTempAssembly(_mapping, type, defaultNamespace, location);
        }

        // [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        private void DemandForUserLocationOrEvidence()
        {
            // Ensure full trust before asserting full file access to the user-provided location or evidence
        }

        internal static TempAssembly GenerateTempAssembly(XmlMapping xmlMapping)
        {
            return GenerateTempAssembly(xmlMapping, null, null);
        }

        internal static TempAssembly GenerateTempAssembly(XmlMapping xmlMapping, Type type, string defaultNamespace)
        {
            if (xmlMapping == null)
                throw new ArgumentNullException("xmlMapping");
            return new TempAssembly(new XmlMapping[] { xmlMapping }, new Type[] { type }, defaultNamespace, null);
        }

        internal static TempAssembly GenerateTempAssembly(XmlMapping xmlMapping, Type type, string defaultNamespace, string location)
        {
            return new TempAssembly(new XmlMapping[] { xmlMapping }, new Type[] { type }, defaultNamespace, location);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(TextWriter textWriter, object o)
        {
            Serialize(textWriter, o, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize1"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(TextWriter textWriter, object o, XmlSerializerNamespaces namespaces)
        {
            XmlTextWriter xmlWriter = new XmlTextWriter(textWriter);
            xmlWriter.Formatting = Formatting.Indented;
            xmlWriter.Indentation = 2;
            Serialize(xmlWriter, o, namespaces);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize2"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(Stream stream, object o)
        {
            Serialize(stream, o, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize3"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(Stream stream, object o, XmlSerializerNamespaces namespaces)
        {
            XmlTextWriter xmlWriter = new XmlTextWriter(stream, null);
            xmlWriter.Formatting = Formatting.Indented;
            xmlWriter.Indentation = 2;
            Serialize(xmlWriter, o, namespaces);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize4"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(XmlWriter xmlWriter, object o)
        {
            Serialize(xmlWriter, o, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize5"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public void Serialize(XmlWriter xmlWriter, object o, XmlSerializerNamespaces namespaces)
        {
            Serialize(xmlWriter, o, namespaces, null);
        }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize6"]/*' />
        public void Serialize(XmlWriter xmlWriter, object o, XmlSerializerNamespaces namespaces, string encodingStyle)
        {
            Serialize(xmlWriter, o, namespaces, encodingStyle, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize6"]/*' />
        public void Serialize(XmlWriter xmlWriter, object o, XmlSerializerNamespaces namespaces, string encodingStyle, string id)
        {
            try
            {
                if (_primitiveType != null)
                {
                    if (encodingStyle != null && encodingStyle.Length > 0)
                    {
                        throw new InvalidOperationException(string.Format(ResXml.XmlInvalidEncodingNotEncoded1, encodingStyle));
                    }
                    SerializePrimitive(xmlWriter, o, namespaces);
                }
                else if (_tempAssembly == null || _typedSerializer)
                {
                    XmlSerializationWriter writer = CreateWriter();
                    writer.Init(xmlWriter, namespaces == null || namespaces.Count == 0 ? DefaultNamespaces : namespaces, encodingStyle, id, _tempAssembly);
                    try
                    {
                        Serialize(o, writer);
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                }
                else
                    _tempAssembly.InvokeWriter(_mapping, xmlWriter, o, namespaces == null || namespaces.Count == 0 ? DefaultNamespaces : namespaces, encodingStyle, id);
            }
            catch (Exception e)
            {
                if (/*e is ThreadAbortException || e is StackOverflowException ||*/ e is OutOfMemoryException)
                {
                    throw;
                }
                if (e is TargetInvocationException)
                    e = e.InnerException;
                throw new InvalidOperationException(ResXml.XmlGenError, e);
            }
            xmlWriter.Flush();
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public object Deserialize(Stream stream)
        {
            XmlTextReader xmlReader = new XmlTextReader(stream);
            xmlReader.WhitespaceHandling = WhitespaceHandling.Significant;
            xmlReader.Normalization = true;
            xmlReader.XmlResolver = null;
            return Deserialize(xmlReader, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize1"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public object Deserialize(TextReader textReader)
        {
            XmlTextReader xmlReader = new XmlTextReader(textReader);
            xmlReader.WhitespaceHandling = WhitespaceHandling.Significant;
            xmlReader.Normalization = true;
            xmlReader.XmlResolver = null;
            return Deserialize(xmlReader, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize2"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public object Deserialize(XmlReader xmlReader)
        {
            return Deserialize(xmlReader, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize3"]/*' />
        public object Deserialize(XmlReader xmlReader, XmlDeserializationEvents events)
        {
            return Deserialize(xmlReader, null, events);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize4"]/*' />
        public object Deserialize(XmlReader xmlReader, string encodingStyle)
        {
            return Deserialize(xmlReader, encodingStyle, _events);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize5"]/*' />
        public object Deserialize(XmlReader xmlReader, string encodingStyle, XmlDeserializationEvents events)
        {
            events.sender = this;
            try
            {
                if (_primitiveType != null)
                {
                    if (encodingStyle != null && encodingStyle.Length > 0)
                    {
                        throw new InvalidOperationException(string.Format(ResXml.XmlInvalidEncodingNotEncoded1, encodingStyle));
                    }
                    return DeserializePrimitive(xmlReader, events);
                }
                else if (_tempAssembly == null || _typedSerializer)
                {
                    XmlSerializationReader reader = CreateReader();
                    reader.Init(xmlReader, events, encodingStyle, _tempAssembly);
                    try
                    {
                        return Deserialize(reader);
                    }
                    finally
                    {
                        reader.Dispose();
                    }
                }
                else
                {
                    return _tempAssembly.InvokeReader(_mapping, xmlReader, events, encodingStyle);
                }
            }
            catch (Exception e)
            {
                if (/*e is ThreadAbortException || e is StackOverflowException ||*/ e is OutOfMemoryException)
                {
                    throw;
                }
                if (e is TargetInvocationException)
                    e = e.InnerException;

                if (xmlReader is IXmlLineInfo)
                {
                    IXmlLineInfo lineInfo = (IXmlLineInfo)xmlReader;
                    throw new InvalidOperationException(string.Format(ResXml.XmlSerializeErrorDetails, lineInfo.LineNumber.ToString(), lineInfo.LinePosition.ToString()), e);
                }
                else
                {
                    throw new InvalidOperationException(ResXml.XmlSerializeError, e);
                }
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.CanDeserialize"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public virtual bool CanDeserialize(XmlReader xmlReader)
        {
            if (_primitiveType != null)
            {
                TypeDesc typeDesc = (TypeDesc)TypeScope.PrimtiveTypes[_primitiveType];
                return xmlReader.IsStartElement(typeDesc.DataType.Name, string.Empty);
            }
            else if (_tempAssembly != null)
            {
                return _tempAssembly.CanRead(_mapping, xmlReader);
            }
            else
            {
                return false;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.FromMappings"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        // [PermissionSet(SecurityAction.LinkDemand, Name="FullTrust")]
        public static XmlSerializer[] FromMappings(XmlMapping[] mappings)
        {
            return FromMappings(mappings, (Type)null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.FromMappings1"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        // [PermissionSet(SecurityAction.LinkDemand, Name="FullTrust")]
        public static XmlSerializer[] FromMappings(XmlMapping[] mappings, Type type)
        {
            if (mappings == null || mappings.Length == 0) return new XmlSerializer[0];
            XmlSerializerImplementation contract = null;
            Assembly assembly = type == null ? null : TempAssembly.LoadGeneratedAssembly(type, null, out contract);
            TempAssembly tempAssembly = null;
            if (assembly == null)
            {
                if (XmlMapping.IsShallow(mappings))
                {
                    return new XmlSerializer[0];
                }
                else
                {
                    if (type == null)
                    {
                        tempAssembly = new TempAssembly(mappings, new Type[] { type }, null, null);
                        XmlSerializer[] serializers = new XmlSerializer[mappings.Length];

                        contract = tempAssembly.Contract;

                        for (int i = 0; i < serializers.Length; i++)
                        {
                            serializers[i] = (XmlSerializer)contract.TypedSerializers[mappings[i].Key];
                            serializers[i].SetTempAssembly(tempAssembly, mappings[i]);
                        }

                        return serializers;
                    }
                    else
                    {
                        // Use XmlSerializer cache when the type is not null.
                        return GetSerializersFromCache(mappings, type);
                    }
                }
            }
            else
            {
                XmlSerializer[] serializers = new XmlSerializer[mappings.Length];
                for (int i = 0; i < serializers.Length; i++)
                    serializers[i] = (XmlSerializer)contract.TypedSerializers[mappings[i].Key];
                return serializers;
            }
        }

        private static XmlSerializer[] GetSerializersFromCache(XmlMapping[] mappings, Type type)
        {
            XmlSerializer[] serializers = new XmlSerializer[mappings.Length];

            Hashtable typedMappingTable = null;
            lock (s_xmlSerializerTable)
            {
                typedMappingTable = s_xmlSerializerTable[type] as Hashtable;
                if (typedMappingTable == null)
                {
                    typedMappingTable = new Hashtable();
                    s_xmlSerializerTable[type] = typedMappingTable;
                }
            }

            lock (typedMappingTable)
            {
                Hashtable pendingKeys = new Hashtable();
                for (int i = 0; i < mappings.Length; i++)
                {
                    XmlSerializerMappingKey mappingKey = new XmlSerializerMappingKey(mappings[i]);
                    serializers[i] = typedMappingTable[mappingKey] as XmlSerializer;
                    if (serializers[i] == null)
                    {
                        pendingKeys.Add(mappingKey, i);
                    }
                }

                if (pendingKeys.Count > 0)
                {
                    XmlMapping[] pendingMappings = new XmlMapping[pendingKeys.Count];
                    int index = 0;
                    foreach (XmlSerializerMappingKey mappingKey in pendingKeys.Keys)
                    {
                        pendingMappings[index++] = mappingKey.Mapping;
                    }

                    TempAssembly tempAssembly = new TempAssembly(pendingMappings, new Type[] { type }, null, null);
                    XmlSerializerImplementation contract = tempAssembly.Contract;

                    foreach (XmlSerializerMappingKey mappingKey in pendingKeys.Keys)
                    {
                        index = (int)pendingKeys[mappingKey];
                        serializers[index] = (XmlSerializer)contract.TypedSerializers[mappingKey.Mapping.Key];
                        serializers[index].SetTempAssembly(tempAssembly, mappingKey.Mapping);

                        typedMappingTable[mappingKey] = serializers[index];
                    }
                }
            }

            return serializers;
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.FromTypes"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public static XmlSerializer[] FromTypes(Type[] types)
        {
            if (types == null)
                return new XmlSerializer[0];
            XmlReflectionImporter importer = new XmlReflectionImporter();
            XmlTypeMapping[] mappings = new XmlTypeMapping[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                mappings[i] = importer.ImportTypeMapping(types[i]);
            }
            return FromMappings(mappings);
        }


        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.GetXmlSerializerAssemblyName"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        // [PermissionSet(SecurityAction.Demand, Name="FullTrust")]
        public static string GetXmlSerializerAssemblyName(Type type)
        {
            return GetXmlSerializerAssemblyName(type, null);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.GetXmlSerializerAssemblyName"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        // [PermissionSet(SecurityAction.Demand, Name="FullTrust")]
        public static string GetXmlSerializerAssemblyName(Type type, string defaultNamespace)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            return Compiler.GetTempAssemblyName(type.GetTypeInfo().Assembly.GetName(), defaultNamespace);
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.UnknownNode"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public event XmlNodeEventHandler UnknownNode
        {
            add
            {
                _events.OnUnknownNode += value;
            }
            remove
            {
                _events.OnUnknownNode -= value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.UnknownAttribute"]/*' />
        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public event XmlAttributeEventHandler UnknownAttribute
        {
            add
            {
                _events.OnUnknownAttribute += value;
            }
            remove
            {
                _events.OnUnknownAttribute -= value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.UnknownElement"]/*' />
        public event XmlElementEventHandler UnknownElement
        {
            add
            {
                _events.OnUnknownElement += value;
            }
            remove
            {
                _events.OnUnknownElement -= value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.UnreferencedObject"]/*' />
        public event UnreferencedObjectEventHandler UnreferencedObject
        {
            add
            {
                _events.OnUnreferencedObject += value;
            }
            remove
            {
                _events.OnUnreferencedObject -= value;
            }
        }

        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.CreateReader"]/*' />
        ///<internalonly/>
        protected virtual XmlSerializationReader CreateReader() { throw new NotImplementedException(); }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Deserialize4"]/*' />
        ///<internalonly/>
        protected virtual object Deserialize(XmlSerializationReader reader) { throw new NotImplementedException(); }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.CreateWriter"]/*' />
        ///<internalonly/>
        protected virtual XmlSerializationWriter CreateWriter() { throw new NotImplementedException(); }
        /// <include file='doc\XmlSerializer.uex' path='docs/doc[@for="XmlSerializer.Serialize7"]/*' />
        ///<internalonly/>
        protected virtual void Serialize(object o, XmlSerializationWriter writer) { throw new NotImplementedException(); }

        internal void SetTempAssembly(TempAssembly tempAssembly, XmlMapping mapping)
        {
            _tempAssembly = tempAssembly;
            _mapping = mapping;
            _typedSerializer = true;
        }

        private static XmlTypeMapping GetKnownMapping(Type type, string ns)
        {
            if (ns != null && ns != string.Empty)
                return null;
            TypeDesc typeDesc = (TypeDesc)TypeScope.PrimtiveTypes[type];
            if (typeDesc == null)
                return null;
            ElementAccessor element = new ElementAccessor();
            element.Name = typeDesc.DataType.Name;
            XmlTypeMapping mapping = new XmlTypeMapping(null, element);
            mapping.SetKeyInternal(XmlMapping.GenerateKey(type, null, null));
            return mapping;
        }

        private void SerializePrimitive(XmlWriter xmlWriter, object o, XmlSerializerNamespaces namespaces)
        {
            XmlSerializationPrimitiveWriter writer = new XmlSerializationPrimitiveWriter();
            writer.Init(xmlWriter, namespaces, null, null, null);
            switch (Type.GetTypeCode(_primitiveType))
            {
                case TypeCode.String:
                    writer.Write_string(o);
                    break;
                case TypeCode.Int32:
                    writer.Write_int(o);
                    break;
                case TypeCode.Boolean:
                    writer.Write_boolean(o);
                    break;
                case TypeCode.Int16:
                    writer.Write_short(o);
                    break;
                case TypeCode.Int64:
                    writer.Write_long(o);
                    break;
                case TypeCode.Single:
                    writer.Write_float(o);
                    break;
                case TypeCode.Double:
                    writer.Write_double(o);
                    break;
                case TypeCode.Decimal:
                    writer.Write_decimal(o);
                    break;
                case TypeCode.DateTime:
                    writer.Write_dateTime(o);
                    break;
                case TypeCode.Char:
                    writer.Write_char(o);
                    break;
                case TypeCode.Byte:
                    writer.Write_unsignedByte(o);
                    break;
                case TypeCode.SByte:
                    writer.Write_byte(o);
                    break;
                case TypeCode.UInt16:
                    writer.Write_unsignedShort(o);
                    break;
                case TypeCode.UInt32:
                    writer.Write_unsignedInt(o);
                    break;
                case TypeCode.UInt64:
                    writer.Write_unsignedLong(o);
                    break;

                default:
                    if (_primitiveType == typeof(XmlQualifiedName))
                    {
                        writer.Write_QName(o);
                    }
                    else if (_primitiveType == typeof(byte[]))
                    {
                        writer.Write_base64Binary(o);
                    }
                    else if (_primitiveType == typeof(Guid))
                    {
                        writer.Write_guid(o);
                    }
                    else
                    {
                        throw new InvalidOperationException(string.Format(ResXml.XmlUnxpectedType, _primitiveType.FullName));
                    }
                    break;
            }
        }

        private object DeserializePrimitive(XmlReader xmlReader, XmlDeserializationEvents events)
        {
            XmlSerializationPrimitiveReader reader = new XmlSerializationPrimitiveReader();
            reader.Init(xmlReader, events, null, null);
            object o;
            switch (Type.GetTypeCode(_primitiveType))
            {
                case TypeCode.String:
                    o = reader.Read_string();
                    break;
                case TypeCode.Int32:
                    o = reader.Read_int();
                    break;
                case TypeCode.Boolean:
                    o = reader.Read_boolean();
                    break;
                case TypeCode.Int16:
                    o = reader.Read_short();
                    break;
                case TypeCode.Int64:
                    o = reader.Read_long();
                    break;
                case TypeCode.Single:
                    o = reader.Read_float();
                    break;
                case TypeCode.Double:
                    o = reader.Read_double();
                    break;
                case TypeCode.Decimal:
                    o = reader.Read_decimal();
                    break;
                case TypeCode.DateTime:
                    o = reader.Read_dateTime();
                    break;
                case TypeCode.Char:
                    o = reader.Read_char();
                    break;
                case TypeCode.Byte:
                    o = reader.Read_unsignedByte();
                    break;
                case TypeCode.SByte:
                    o = reader.Read_byte();
                    break;
                case TypeCode.UInt16:
                    o = reader.Read_unsignedShort();
                    break;
                case TypeCode.UInt32:
                    o = reader.Read_unsignedInt();
                    break;
                case TypeCode.UInt64:
                    o = reader.Read_unsignedLong();
                    break;

                default:
                    if (_primitiveType == typeof(XmlQualifiedName))
                    {
                        o = reader.Read_QName();
                    }
                    else if (_primitiveType == typeof(byte[]))
                    {
                        o = reader.Read_base64Binary();
                    }
                    else if (_primitiveType == typeof(Guid))
                    {
                        o = reader.Read_guid();
                    }
                    else
                    {
                        throw new InvalidOperationException(string.Format(ResXml.XmlUnxpectedType, _primitiveType.FullName));
                    }
                    break;
            }
            return o;
        }

        private class XmlSerializerMappingKey
        {
            public XmlMapping Mapping;
            public XmlSerializerMappingKey(XmlMapping mapping)
            {
                this.Mapping = mapping;
            }

            public override bool Equals(object obj)
            {
                XmlSerializerMappingKey other = obj as XmlSerializerMappingKey;
                if (other == null)
                    return false;

                if (this.Mapping.Key != other.Mapping.Key)
                    return false;

                if (this.Mapping.ElementName != other.Mapping.ElementName)
                    return false;

                if (this.Mapping.Namespace != other.Mapping.Namespace)
                    return false;

                if (this.Mapping.IsSoap != other.Mapping.IsSoap)
                    return false;

                return true;
            }

            public override int GetHashCode()
            {
                int hashCode = this.Mapping.IsSoap ? 0 : 1;

                if (this.Mapping.Key != null)
                    hashCode ^= this.Mapping.Key.GetHashCode();

                if (this.Mapping.ElementName != null)
                    hashCode ^= this.Mapping.ElementName.GetHashCode();

                if (this.Mapping.Namespace != null)
                    hashCode ^= this.Mapping.Namespace.GetHashCode();

                return hashCode;
            }
        }
    }
}
