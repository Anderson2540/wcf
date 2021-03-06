// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Runtime.Serialization
{
    using System;
    using Microsoft.CodeDom;
    using Microsoft.CodeDom.Compiler;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.Reflection;
    using System.Security;
    using System.Text;
    using Microsoft.Xml;
    using Microsoft.Xml.Schema;
    using DataContractDictionary = System.Collections.Generic.Dictionary<Microsoft.Xml.XmlQualifiedName, DataContract>;

    internal class CodeExporter
    {
        private DataContractSet _dataContractSet;
        private CodeCompileUnit _codeCompileUnit;
        private ImportOptions _options;
        private Dictionary<string, string> _namespaces;
        private Dictionary<string, string> _clrNamespaces;

        // [Fx.Tag.SecurityNote(Miscellaneous = "RequiresReview - Static fields are marked SecurityCritical or readonly to prevent"
        //     + " data from being modified or leaked to other components in appdomain.")]
        private static readonly string s_wildcardNamespaceMapping = "*";

        // [Fx.Tag.SecurityNote(Miscellaneous = "RequiresReview - Static fields are marked SecurityCritical or readonly to prevent"
        //     + " data from being modified or leaked to other components in appdomain.")]
        private static readonly string s_typeNameFieldName = "typeName";

        // [Fx.Tag.SecurityNote(Miscellaneous = "RequiresReview - Static fields are marked SecurityCritical or readonly to prevent"
        //     + " data from being modified or leaked to other components in appdomain.")]
        private static readonly object s_codeUserDataActualTypeKey = new object();

        // [Fx.Tag.SecurityNote(Miscellaneous = "RequiresReview - Static fields are marked SecurityCritical or readonly to prevent"
        //     + " data from being modified or leaked to other components in appdomain.")]
        private static readonly object s_surrogateDataKey = typeof(IDataContractSurrogate);
        private const int MaxIdentifierLength = 511;

        internal CodeExporter(DataContractSet dataContractSet, ImportOptions options, CodeCompileUnit codeCompileUnit)
        {
            _dataContractSet = dataContractSet;
            _codeCompileUnit = codeCompileUnit;
            AddReferencedAssembly(typeof(CodeExporter).GetTypeInfo().Assembly);
            _options = options;
            _namespaces = new Dictionary<string, string>();
            _clrNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);

            // Update namespace tables for DataContract(s) that are already processed
            foreach (KeyValuePair<XmlQualifiedName, DataContract> pair in dataContractSet)
            {
                DataContract dataContract = pair.Value;
                if (!(dataContract.IsBuiltInDataContract || dataContract is CollectionDataContract))
                {
                    ContractCodeDomInfo contractCodeDomInfo = GetContractCodeDomInfo(dataContract);
                    if (contractCodeDomInfo.IsProcessed && !contractCodeDomInfo.UsesWildcardNamespace)
                    {
                        string clrNamespace = contractCodeDomInfo.ClrNamespace;
                        if (clrNamespace != null && !_clrNamespaces.ContainsKey(clrNamespace))
                        {
                            _clrNamespaces.Add(clrNamespace, dataContract.StableName.Namespace);
                            _namespaces.Add(dataContract.StableName.Namespace, clrNamespace);
                        }
                    }
                }
            }

            // Copy options.Namespaces to namespace tables
            if (_options != null)
            {
                foreach (KeyValuePair<string, string> pair in options.Namespaces)
                {
                    string dataContractNamespace = pair.Key;
                    string clrNamespace = pair.Value;
                    if (clrNamespace == null)
                        clrNamespace = String.Empty;

                    string currentDataContractNamespace;
                    if (_clrNamespaces.TryGetValue(clrNamespace, out currentDataContractNamespace))
                    {
                        if (dataContractNamespace != currentDataContractNamespace)
                            throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.CLRNamespaceMappedMultipleTimes, currentDataContractNamespace, dataContractNamespace, clrNamespace)));
                    }
                    else
                        _clrNamespaces.Add(clrNamespace, dataContractNamespace);

                    string currentClrNamespace;
                    if (_namespaces.TryGetValue(dataContractNamespace, out currentClrNamespace))
                    {
                        if (clrNamespace != currentClrNamespace)
                        {
                            _namespaces.Remove(dataContractNamespace);
                            _namespaces.Add(dataContractNamespace, clrNamespace);
                        }
                    }
                    else
                        _namespaces.Add(dataContractNamespace, clrNamespace);
                }
            }

            // Update namespace tables for pre-existing namespaces in CodeCompileUnit
            foreach (CodeNamespace codeNS in codeCompileUnit.Namespaces)
            {
                string ns = codeNS.Name ?? string.Empty;
                if (!_clrNamespaces.ContainsKey(ns))
                {
                    _clrNamespaces.Add(ns, null);
                }
                if (ns.Length == 0)
                {
                    foreach (CodeTypeDeclaration codeTypeDecl in codeNS.Types)
                    {
                        AddGlobalTypeName(codeTypeDecl.Name);
                    }
                }
            }
        }

        private void AddReferencedAssembly(Assembly assembly)
        {
            string assemblyName = System.IO.Path.GetFileName(assembly.Location);
            bool alreadyExisting = false;
            foreach (string existingName in _codeCompileUnit.ReferencedAssemblies)
            {
                if (String.Compare(existingName, assemblyName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    alreadyExisting = true;
                    break;
                }
            }
            if (!alreadyExisting)
                _codeCompileUnit.ReferencedAssemblies.Add(assemblyName);
        }

        private bool GenerateSerializableTypes
        {
            get { return (_options == null) ? false : _options.GenerateSerializable; }
        }

        private bool GenerateInternalTypes
        {
            get { return (_options == null) ? false : _options.GenerateInternal; }
        }

        private bool EnableDataBinding
        {
            get { return (_options == null) ? false : _options.EnableDataBinding; }
        }

        private CodeDomProvider CodeProvider
        {
            get { return (_options == null) ? null : _options.CodeProvider; }
        }

        private bool SupportsDeclareEvents
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.DeclareEvents); }
        }

        private bool SupportsDeclareValueTypes
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.DeclareValueTypes); }
        }

        private bool SupportsGenericTypeReference
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.GenericTypeReference); }
        }

        private bool SupportsAssemblyAttributes
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.AssemblyAttributes); }
        }

        private bool SupportsPartialTypes
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.PartialTypes); }
        }

        private bool SupportsNestedTypes
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.Supports(..) method that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? true : CodeProvider.Supports(GeneratorSupport.NestedTypes); }
        }

        private string FileExtension
        {
            // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeProvider.FileExtension property that has a LinkDemand.",
            //     Safe = "Safe because it doesn't leak security sensitive information.")]
            [SecuritySafeCritical]
            get { return (CodeProvider == null) ? String.Empty : CodeProvider.FileExtension; }
        }

        private Dictionary<string, string> Namespaces
        {
            get { return _namespaces; }
        }

        private Dictionary<string, string> ClrNamespaces
        {
            get { return _clrNamespaces; }
        }


        private bool TryGetReferencedType(XmlQualifiedName stableName, DataContract dataContract, out Type type)
        {
            if (dataContract == null)
            {
                if (_dataContractSet.TryGetReferencedCollectionType(stableName, dataContract, out type))
                    return true;
                if (_dataContractSet.TryGetReferencedType(stableName, dataContract, out type))
                {
                    // enforce that collection types only be specified via ReferencedCollectionTypes 
                    if (CollectionDataContract.IsCollection(type))
                    {
                        type = null;
                        return false;
                    }
                    return true;
                }
                return false;
            }
            else if (dataContract is CollectionDataContract)
                return _dataContractSet.TryGetReferencedCollectionType(stableName, dataContract, out type);
            else
            {
                XmlDataContract xmlDataContract = dataContract as XmlDataContract;
                if (xmlDataContract != null && xmlDataContract.IsAnonymous)
                {
                    stableName = SchemaImporter.ImportActualType(xmlDataContract.XsdType.Annotation, stableName, dataContract.StableName);
                }
                return _dataContractSet.TryGetReferencedType(stableName, dataContract, out type);
            }
        }

        // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the System.CodeDom.Compiler.CodeGenerator.ValidateIdentifiers(..) method that has a LinkDemand for FullTrust.")]
        [SecurityCritical]
        internal void Export()
        {
            try
            {
                foreach (KeyValuePair<XmlQualifiedName, DataContract> pair in _dataContractSet)
                {
                    DataContract dataContract = pair.Value;
                    if (dataContract.IsBuiltInDataContract)
                        continue;

                    ContractCodeDomInfo contractCodeDomInfo = GetContractCodeDomInfo(dataContract);
                    if (!contractCodeDomInfo.IsProcessed)
                    {
                        if (dataContract is ClassDataContract)
                        {
                            ClassDataContract classDataContract = (ClassDataContract)dataContract;
                            ExportClassDataContractHierarchy(classDataContract.StableName, classDataContract, contractCodeDomInfo, new Dictionary<XmlQualifiedName, object>());
                        }
                        else if (dataContract is CollectionDataContract)
                            ExportCollectionDataContract((CollectionDataContract)dataContract, contractCodeDomInfo);
                        else if (dataContract is EnumDataContract)
                            ExportEnumDataContract((EnumDataContract)dataContract, contractCodeDomInfo);
                        else if (dataContract is XmlDataContract)
                            ExportXmlDataContract((XmlDataContract)dataContract, contractCodeDomInfo);
                        else
                            throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.UnexpectedContractType, DataContract.GetClrTypeFullName(dataContract.GetType()), DataContract.GetClrTypeFullName(dataContract.UnderlyingType))));
                        contractCodeDomInfo.IsProcessed = true;
                    }
                }
                if (_dataContractSet.DataContractSurrogate != null)
                {
                    CodeNamespace[] namespaces = new CodeNamespace[_codeCompileUnit.Namespaces.Count];
                    _codeCompileUnit.Namespaces.CopyTo(namespaces, 0);
                    foreach (CodeNamespace codeNamespace in namespaces)
                        InvokeProcessImportedType(codeNamespace.Types);
                }
            }
            finally
            {
                Microsoft.CodeDom.Compiler.CodeGenerator.ValidateIdentifiers(_codeCompileUnit);
            }
        }

        private void ExportClassDataContractHierarchy(XmlQualifiedName typeName, ClassDataContract classContract, ContractCodeDomInfo contractCodeDomInfo, Dictionary<XmlQualifiedName, object> contractNamesInHierarchy)
        {
            if (contractNamesInHierarchy.ContainsKey(classContract.StableName))
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.TypeCannotBeImported, typeName.Name, typeName.Namespace, string.Format(SRSerialization.CircularTypeReference, classContract.StableName.Name, classContract.StableName.Namespace))));
            contractNamesInHierarchy.Add(classContract.StableName, null);

            ClassDataContract baseContract = classContract.BaseContract;
            if (baseContract != null)
            {
                ContractCodeDomInfo baseContractCodeDomInfo = GetContractCodeDomInfo(baseContract);
                if (!baseContractCodeDomInfo.IsProcessed)
                {
                    ExportClassDataContractHierarchy(typeName, baseContract, baseContractCodeDomInfo, contractNamesInHierarchy);
                    baseContractCodeDomInfo.IsProcessed = true;
                }
            }
            ExportClassDataContract(classContract, contractCodeDomInfo);
        }

        private void InvokeProcessImportedType(CollectionBase collection)
        {
            object[] objects = new object[collection.Count];
            ((ICollection)collection).CopyTo(objects, 0);
            foreach (object obj in objects)
            {
                CodeTypeDeclaration codeTypeDeclaration = obj as CodeTypeDeclaration;
                if (codeTypeDeclaration == null)
                    continue;

                CodeTypeDeclaration newCodeTypeDeclaration = DataContractSurrogateCaller.ProcessImportedType(
                                                                   _dataContractSet.DataContractSurrogate,
                                                                   codeTypeDeclaration,
                                                                   _codeCompileUnit);
                if (newCodeTypeDeclaration != codeTypeDeclaration)
                {
                    ((IList)collection).Remove(codeTypeDeclaration);
                    if (newCodeTypeDeclaration != null)
                        ((IList)collection).Add(newCodeTypeDeclaration);
                }
                if (newCodeTypeDeclaration != null)
                    InvokeProcessImportedType(newCodeTypeDeclaration.Members);
            }
        }

        internal CodeTypeReference GetCodeTypeReference(DataContract dataContract)
        {
            if (dataContract.IsBuiltInDataContract)
                return GetCodeTypeReference(dataContract.UnderlyingType);

            ContractCodeDomInfo contractCodeDomInfo = GetContractCodeDomInfo(dataContract);
            GenerateType(dataContract, contractCodeDomInfo);
            return contractCodeDomInfo.TypeReference;
        }

        private CodeTypeReference GetCodeTypeReference(Type type)
        {
            AddReferencedAssembly(type.GetTypeInfo().Assembly);
            return new CodeTypeReference(type);
        }

        internal CodeTypeReference GetElementTypeReference(DataContract dataContract, bool isElementTypeNullable)
        {
            CodeTypeReference elementTypeReference = GetCodeTypeReference(dataContract);
            if (dataContract.IsValueType && isElementTypeNullable)
                elementTypeReference = WrapNullable(elementTypeReference);
            return elementTypeReference;
        }

        private XmlQualifiedName GenericListName
        {
            get { return DataContract.GetStableName(Globals.TypeOfListGeneric); }
        }

        private CollectionDataContract GenericListContract
        {
            get { return _dataContractSet.GetDataContract(Globals.TypeOfListGeneric) as CollectionDataContract; }
        }

        private XmlQualifiedName GenericDictionaryName
        {
            get { return DataContract.GetStableName(Globals.TypeOfDictionaryGeneric); }
        }

        private CollectionDataContract GenericDictionaryContract
        {
            get { return _dataContractSet.GetDataContract(Globals.TypeOfDictionaryGeneric) as CollectionDataContract; }
        }

        private ContractCodeDomInfo GetContractCodeDomInfo(DataContract dataContract)
        {
            ContractCodeDomInfo contractCodeDomInfo = _dataContractSet.GetContractCodeDomInfo(dataContract);
            if (contractCodeDomInfo == null)
            {
                contractCodeDomInfo = new ContractCodeDomInfo();
                _dataContractSet.SetContractCodeDomInfo(dataContract, contractCodeDomInfo);
            }
            return contractCodeDomInfo;
        }

        private void GenerateType(DataContract dataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            if (!contractCodeDomInfo.IsProcessed)
            {
                CodeTypeReference referencedType = GetReferencedType(dataContract);
                if (referencedType != null)
                {
                    contractCodeDomInfo.TypeReference = referencedType;
                    contractCodeDomInfo.ReferencedTypeExists = true;
                }
                else
                {
                    CodeTypeDeclaration type = contractCodeDomInfo.TypeDeclaration;
                    if (type == null)
                    {
                        string clrNamespace = GetClrNamespace(dataContract, contractCodeDomInfo);
                        CodeNamespace ns = GetCodeNamespace(clrNamespace, dataContract.StableName.Namespace, contractCodeDomInfo);
                        type = GetNestedType(dataContract, contractCodeDomInfo);
                        if (type == null)
                        {
                            string typeName = XmlConvert.DecodeName(dataContract.StableName.Name);
                            typeName = GetClrIdentifier(typeName, Globals.DefaultTypeName);
                            if (NamespaceContainsType(ns, typeName) || GlobalTypeNameConflicts(clrNamespace, typeName))
                            {
                                for (int i = 1; ; i++)
                                {
                                    string uniqueName = AppendToValidClrIdentifier(typeName, i.ToString(NumberFormatInfo.InvariantInfo));
                                    if (!NamespaceContainsType(ns, uniqueName) && !GlobalTypeNameConflicts(clrNamespace, uniqueName))
                                    {
                                        typeName = uniqueName;
                                        break;
                                    }
                                    if (i == Int32.MaxValue)
                                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.CannotComputeUniqueName, typeName)));
                                }
                            }

                            type = CreateTypeDeclaration(typeName, dataContract);
                            ns.Types.Add(type);
                            if (string.IsNullOrEmpty(clrNamespace))
                            {
                                AddGlobalTypeName(typeName);
                            }
                            contractCodeDomInfo.TypeReference = new CodeTypeReference((clrNamespace == null || clrNamespace.Length == 0) ? typeName : clrNamespace + "." + typeName);

                            if (GenerateInternalTypes)
                                type.TypeAttributes = TypeAttributes.NotPublic;
                            else
                                type.TypeAttributes = TypeAttributes.Public;
                        }
                        if (_dataContractSet.DataContractSurrogate != null)
                            type.UserData.Add(s_surrogateDataKey, _dataContractSet.GetSurrogateData(dataContract));

                        contractCodeDomInfo.TypeDeclaration = type;
                    }
                }
            }
        }

        private CodeTypeDeclaration GetNestedType(DataContract dataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            if (!SupportsNestedTypes)
                return null;
            string originalName = dataContract.StableName.Name;
            int nestedTypeIndex = originalName.LastIndexOf('.');
            if (nestedTypeIndex <= 0)
                return null;
            string containingTypeName = originalName.Substring(0, nestedTypeIndex);
            DataContract containingDataContract = _dataContractSet[new XmlQualifiedName(containingTypeName, dataContract.StableName.Namespace)];
            if (containingDataContract == null)
                return null;
            string nestedTypeName = XmlConvert.DecodeName(originalName.Substring(nestedTypeIndex + 1));
            nestedTypeName = GetClrIdentifier(nestedTypeName, Globals.DefaultTypeName);

            ContractCodeDomInfo containingContractCodeDomInfo = GetContractCodeDomInfo(containingDataContract);
            GenerateType(containingDataContract, containingContractCodeDomInfo);
            if (containingContractCodeDomInfo.ReferencedTypeExists)
                return null;

            CodeTypeDeclaration containingType = containingContractCodeDomInfo.TypeDeclaration;
            if (TypeContainsNestedType(containingType, nestedTypeName))
            {
                for (int i = 1; ; i++)
                {
                    string uniqueName = AppendToValidClrIdentifier(nestedTypeName, i.ToString(NumberFormatInfo.InvariantInfo));
                    if (!TypeContainsNestedType(containingType, uniqueName))
                    {
                        nestedTypeName = uniqueName;
                        break;
                    }
                }
            }

            CodeTypeDeclaration type = CreateTypeDeclaration(nestedTypeName, dataContract);
            containingType.Members.Add(type);
            contractCodeDomInfo.TypeReference = new CodeTypeReference(containingContractCodeDomInfo.TypeReference.BaseType + "+" + nestedTypeName);

            if (GenerateInternalTypes)
                type.TypeAttributes = TypeAttributes.NestedAssembly;
            else
                type.TypeAttributes = TypeAttributes.NestedPublic;
            return type;
        }

        private static CodeTypeDeclaration CreateTypeDeclaration(string typeName, DataContract dataContract)
        {
            CodeTypeDeclaration typeDecl = new CodeTypeDeclaration(typeName);
            CodeAttributeDeclaration debuggerStepThroughAttribute = new CodeAttributeDeclaration(typeof(System.Diagnostics.DebuggerStepThroughAttribute).FullName);
            CodeAttributeDeclaration generatedCodeAttribute = new CodeAttributeDeclaration(typeof(GeneratedCodeAttribute).FullName);

            AssemblyName assemblyName = typeof(CodeExporter).GetTypeInfo().Assembly.GetName();
            generatedCodeAttribute.Arguments.Add(new CodeAttributeArgument(new CodePrimitiveExpression(assemblyName.Name)));
            generatedCodeAttribute.Arguments.Add(new CodeAttributeArgument(new CodePrimitiveExpression(assemblyName.Version.ToString())));

            // System.Diagnostics.DebuggerStepThroughAttribute not allowed on enums
            // ensure that the attribute is only generated on types that are not enums
            EnumDataContract enumDataContract = dataContract as EnumDataContract;
            if (enumDataContract == null)
            {
                typeDecl.CustomAttributes.Add(debuggerStepThroughAttribute);
            }
            typeDecl.CustomAttributes.Add(generatedCodeAttribute);
            return typeDecl;
        }

        // [Fx.Tag.SecurityNote(Critical = "Sets critical properties on internal XmlDataContract.",
        //     Safe = "Called during schema import/code generation.")]
        [SecuritySafeCritical]
        private CodeTypeReference GetReferencedType(DataContract dataContract)
        {
            Type type = null;
            CodeTypeReference typeReference = GetSurrogatedTypeReference(dataContract);
            if (typeReference != null)
                return typeReference;

            TypeInfo info;

            if (TryGetReferencedType(dataContract.StableName, dataContract, out type) && (info = type.GetTypeInfo()) != null
                && !info.IsGenericTypeDefinition && !info.ContainsGenericParameters)
            {
                if (dataContract is XmlDataContract)
                {
                    if (Globals.TypeOfIXmlSerializable.IsAssignableFrom(type))
                    {
                        XmlDataContract xmlContract = (XmlDataContract)dataContract;
                        if (xmlContract.IsTypeDefinedOnImport)
                        {
                            if (!xmlContract.Equals(_dataContractSet.GetDataContract(type)))
                                throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.ReferencedTypeDoesNotMatch, type.AssemblyQualifiedName, dataContract.StableName.Name, dataContract.StableName.Namespace)));
                        }
                        else
                        {
                            xmlContract.IsValueType = info.IsValueType;
                            xmlContract.IsTypeDefinedOnImport = true;
                        }
                        return GetCodeTypeReference(type);
                    }
                    throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.TypeMustBeIXmlSerializable, DataContract.GetClrTypeFullName(type), DataContract.GetClrTypeFullName(Globals.TypeOfIXmlSerializable), dataContract.StableName.Name, dataContract.StableName.Namespace)));
                }
                DataContract referencedContract = _dataContractSet.GetDataContract(type);
                if (referencedContract.Equals(dataContract))
                {
                    typeReference = GetCodeTypeReference(type);
                    typeReference.UserData.Add(s_codeUserDataActualTypeKey, type);
                    return typeReference;
                }
                throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.ReferencedTypeDoesNotMatch, type.AssemblyQualifiedName, dataContract.StableName.Name, dataContract.StableName.Namespace)));
            }
            else if (dataContract.GenericInfo != null)
            {
                DataContract referencedContract;
                XmlQualifiedName genericStableName = dataContract.GenericInfo.GetExpandedStableName();
                if (genericStableName != dataContract.StableName)
                    throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.GenericTypeNameMismatch, dataContract.StableName.Name, dataContract.StableName.Namespace, genericStableName.Name, genericStableName.Namespace)));

                typeReference = GetReferencedGenericType(dataContract.GenericInfo, out referencedContract);
                // Generic type equivalent to if (referencedContract != null && !referencedContract.Equals(dataContract)) {
                if (referencedContract != null && !(referencedContract.StableName.Equals(dataContract.GenericInfo.StableName) && referencedContract.IsReference == dataContract.IsReference))
                {
                    type = (Type)typeReference.UserData[s_codeUserDataActualTypeKey];
                    throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.ReferencedTypeDoesNotMatch,
                        type.AssemblyQualifiedName,
                        referencedContract.StableName.Name,
                        referencedContract.StableName.Namespace)));
                }
                return typeReference;
            }

            return GetReferencedCollectionType(dataContract as CollectionDataContract);
        }

        private CodeTypeReference GetReferencedCollectionType(CollectionDataContract collectionContract)
        {
            if (collectionContract == null)
                return null;

            if (HasDefaultCollectionNames(collectionContract))
            {
                CodeTypeReference typeReference;
                if (!TryGetReferencedDictionaryType(collectionContract, out typeReference))
                {
                    DataContract itemContract = collectionContract.ItemContract;
                    if (collectionContract.IsDictionary)
                    {
                        GenerateKeyValueType(itemContract as ClassDataContract);
                    }
                    bool isItemTypeNullable = collectionContract.IsItemTypeNullable;
                    if (!TryGetReferencedListType(itemContract, isItemTypeNullable, out typeReference))
                        typeReference = new CodeTypeReference(GetElementTypeReference(itemContract, isItemTypeNullable), 1);
                }
                return typeReference;
            }
            return null;
        }

        private bool HasDefaultCollectionNames(CollectionDataContract collectionContract)
        {
            DataContract itemContract = collectionContract.ItemContract;
            if (collectionContract.ItemName != itemContract.StableName.Name)
                return false;

            if (collectionContract.IsDictionary &&
                (collectionContract.KeyName != Globals.KeyLocalName || collectionContract.ValueName != Globals.ValueLocalName))
                return false;

            XmlQualifiedName expectedType = itemContract.GetArrayTypeName(collectionContract.IsItemTypeNullable);
            return (collectionContract.StableName.Name == expectedType.Name && collectionContract.StableName.Namespace == expectedType.Namespace);
        }

        private bool TryGetReferencedDictionaryType(CollectionDataContract collectionContract, out CodeTypeReference typeReference)
        {
            // Check if it is a dictionary and use referenced dictionary type if present
            if (collectionContract.IsDictionary
                && SupportsGenericTypeReference)
            {
                Type type;
                if (!TryGetReferencedType(GenericDictionaryName, GenericDictionaryContract, out type))
                    type = Globals.TypeOfDictionaryGeneric;
                ClassDataContract itemContract = collectionContract.ItemContract as ClassDataContract;
                DataMember keyMember = itemContract.Members[0];
                DataMember valueMember = itemContract.Members[1];
                CodeTypeReference keyTypeReference = GetElementTypeReference(keyMember.MemberTypeContract, keyMember.IsNullable);
                CodeTypeReference valueTypeReference = GetElementTypeReference(valueMember.MemberTypeContract, valueMember.IsNullable);
                if (keyTypeReference != null && valueTypeReference != null)
                {
                    typeReference = GetCodeTypeReference(type);
                    typeReference.TypeArguments.Add(keyTypeReference);
                    typeReference.TypeArguments.Add(valueTypeReference);
                    return true;
                }
            }
            typeReference = null;
            return false;
        }

        private bool TryGetReferencedListType(DataContract itemContract, bool isItemTypeNullable, out CodeTypeReference typeReference)
        {
            Type type;
            if (SupportsGenericTypeReference && TryGetReferencedType(GenericListName, GenericListContract, out type))
            {
                typeReference = GetCodeTypeReference(type);
                typeReference.TypeArguments.Add(GetElementTypeReference(itemContract, isItemTypeNullable));
                return true;
            }
            typeReference = null;
            return false;
        }

        private CodeTypeReference GetSurrogatedTypeReference(DataContract dataContract)
        {
            IDataContractSurrogate dataContractSurrogate = _dataContractSet.DataContractSurrogate;
            if (dataContractSurrogate != null)
            {
                Type type = DataContractSurrogateCaller.GetReferencedTypeOnImport(
                        dataContractSurrogate,
                        dataContract.StableName.Name,
                        dataContract.StableName.Namespace,
                        _dataContractSet.GetSurrogateData(dataContract));
                if (type != null)
                {
                    CodeTypeReference typeReference = GetCodeTypeReference(type);
                    typeReference.UserData.Add(s_codeUserDataActualTypeKey, type);
                    return typeReference;
                }
            }
            return null;
        }

        private CodeTypeReference GetReferencedGenericType(GenericInfo genInfo, out DataContract dataContract)
        {
            dataContract = null;

            if (!SupportsGenericTypeReference)
                return null;

            Type type;
            if (!TryGetReferencedType(genInfo.StableName, null, out type))
            {
                if (genInfo.Parameters != null)
                    return null;
                dataContract = _dataContractSet[genInfo.StableName];
                if (dataContract == null)
                    return null;
                if (dataContract.GenericInfo != null)
                    return null;
                return GetCodeTypeReference(dataContract);
            }

            bool enableStructureCheck = (type != Globals.TypeOfNullable);
            CodeTypeReference typeReference = GetCodeTypeReference(type);
            typeReference.UserData.Add(s_codeUserDataActualTypeKey, type);
            if (genInfo.Parameters != null)
            {
                DataContract[] paramContracts = new DataContract[genInfo.Parameters.Count];
                for (int i = 0; i < genInfo.Parameters.Count; i++)
                {
                    GenericInfo paramInfo = genInfo.Parameters[i];
                    XmlQualifiedName stableName = paramInfo.GetExpandedStableName();
                    DataContract paramContract = _dataContractSet[stableName];

                    CodeTypeReference paramTypeReference;
                    bool isParamValueType;
                    if (paramContract != null)
                    {
                        paramTypeReference = GetCodeTypeReference(paramContract);
                        isParamValueType = paramContract.IsValueType;
                    }
                    else
                    {
                        paramTypeReference = GetReferencedGenericType(paramInfo, out paramContract);
                        isParamValueType = (paramTypeReference != null && paramTypeReference.ArrayRank == 0); // only value type information we can get from CodeTypeReference
                    }
                    paramContracts[i] = paramContract;
                    if (paramContract == null)
                        enableStructureCheck = false;
                    if (paramTypeReference == null)
                        return null;
                    if (type == Globals.TypeOfNullable && !isParamValueType)
                        return paramTypeReference;
                    else
                        typeReference.TypeArguments.Add(paramTypeReference);
                }
                if (enableStructureCheck)
                    dataContract = DataContract.GetDataContract(type).BindGenericParameters(paramContracts, new Dictionary<DataContract, DataContract>());
            }
            return typeReference;
        }

        private bool NamespaceContainsType(CodeNamespace ns, string typeName)
        {
            foreach (CodeTypeDeclaration type in ns.Types)
            {
                if (String.Compare(typeName, type.Name, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }

        private bool GlobalTypeNameConflicts(string clrNamespace, string typeName)
        {
            return (string.IsNullOrEmpty(clrNamespace) && _clrNamespaces.ContainsKey(typeName));
        }

        private void AddGlobalTypeName(string typeName)
        {
            if (!_clrNamespaces.ContainsKey(typeName))
            {
                _clrNamespaces.Add(typeName, null);
            }
        }

        private bool TypeContainsNestedType(CodeTypeDeclaration containingType, string typeName)
        {
            foreach (CodeTypeMember member in containingType.Members)
            {
                if (member is CodeTypeDeclaration)
                {
                    if (String.Compare(typeName, ((CodeTypeDeclaration)member).Name, StringComparison.OrdinalIgnoreCase) == 0)
                        return true;
                }
            }
            return false;
        }

        private string GetNameForAttribute(string name)
        {
            string decodedName = XmlConvert.DecodeName(name);
            if (string.CompareOrdinal(name, decodedName) == 0)
                return name;
            string reencodedName = DataContract.EncodeLocalName(decodedName);
            return (string.CompareOrdinal(name, reencodedName) == 0) ? decodedName : name;
        }

        private void AddSerializableAttribute(bool generateSerializable, CodeTypeDeclaration type, ContractCodeDomInfo contractCodeDomInfo)
        {
        }

        private void ExportClassDataContract(ClassDataContract classDataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            GenerateType(classDataContract, contractCodeDomInfo);
            if (contractCodeDomInfo.ReferencedTypeExists)
                return;

            CodeTypeDeclaration type = contractCodeDomInfo.TypeDeclaration;
            if (SupportsPartialTypes)
                type.IsPartial = true;
            if (classDataContract.IsValueType && SupportsDeclareValueTypes)
                type.IsStruct = true;
            else
                type.IsClass = true;

            string dataContractName = GetNameForAttribute(classDataContract.StableName.Name);
            CodeAttributeDeclaration dataContractAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfDataContractAttribute));
            dataContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NameProperty, new CodePrimitiveExpression(dataContractName)));
            dataContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NamespaceProperty, new CodePrimitiveExpression(classDataContract.StableName.Namespace)));
            if (classDataContract.IsReference != Globals.DefaultIsReference)
                dataContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.IsReferenceProperty, new CodePrimitiveExpression(classDataContract.IsReference)));
            type.CustomAttributes.Add(dataContractAttribute);
            AddImportStatement(Globals.TypeOfDataContractAttribute.Namespace, contractCodeDomInfo.CodeNamespace);

            AddSerializableAttribute(GenerateSerializableTypes, type, contractCodeDomInfo);

            AddKnownTypes(classDataContract, contractCodeDomInfo);

            bool raisePropertyChanged = EnableDataBinding && SupportsDeclareEvents;
            if (classDataContract.BaseContract == null)
            {
                if (!type.IsStruct)
                    type.BaseTypes.Add(Globals.TypeOfObject);
                AddExtensionData(contractCodeDomInfo);
                AddPropertyChangedNotifier(contractCodeDomInfo, type.IsStruct);
            }
            else
            {
                ContractCodeDomInfo baseContractCodeDomInfo = GetContractCodeDomInfo(classDataContract.BaseContract);
                Fx.Assert(baseContractCodeDomInfo.IsProcessed, "Cannot generate code for type if code for base type has not been generated");
                type.BaseTypes.Add(baseContractCodeDomInfo.TypeReference);
                AddBaseMemberNames(baseContractCodeDomInfo, contractCodeDomInfo);
                if (baseContractCodeDomInfo.ReferencedTypeExists)
                {
                    Type actualType = (Type)baseContractCodeDomInfo.TypeReference.UserData[s_codeUserDataActualTypeKey];
                    ThrowIfReferencedBaseTypeSealed(actualType, classDataContract);
                    if (!Globals.TypeOfIExtensibleDataObject.IsAssignableFrom(actualType))
                        AddExtensionData(contractCodeDomInfo);
                    if (!Globals.TypeOfIPropertyChange.IsAssignableFrom(actualType))
                    {
                        AddPropertyChangedNotifier(contractCodeDomInfo, type.IsStruct);
                    }
                    else
                    {
                        raisePropertyChanged = false;
                    }
                }
            }

            if (classDataContract.Members != null)
            {
                for (int i = 0; i < classDataContract.Members.Count; i++)
                {
                    DataMember dataMember = classDataContract.Members[i];

                    CodeTypeReference memberType = GetElementTypeReference(dataMember.MemberTypeContract,
                        (dataMember.IsNullable && dataMember.MemberTypeContract.IsValueType));

                    string dataMemberName = GetNameForAttribute(dataMember.Name);
                    string propertyName = GetMemberName(dataMemberName, contractCodeDomInfo);
                    string fieldName = GetMemberName(AppendToValidClrIdentifier(propertyName, Globals.DefaultFieldSuffix), contractCodeDomInfo);

                    CodeMemberField field = new CodeMemberField();
                    field.Type = memberType;
                    field.Name = fieldName;
                    field.Attributes = MemberAttributes.Private;

                    CodeMemberProperty property = CreateProperty(memberType, propertyName, fieldName, dataMember.MemberTypeContract.IsValueType && SupportsDeclareValueTypes, raisePropertyChanged);
                    if (_dataContractSet.DataContractSurrogate != null)
                        property.UserData.Add(s_surrogateDataKey, _dataContractSet.GetSurrogateData(dataMember));

                    CodeAttributeDeclaration dataMemberAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfDataMemberAttribute));
                    if (dataMemberName != property.Name)
                        dataMemberAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NameProperty, new CodePrimitiveExpression(dataMemberName)));
                    if (dataMember.IsRequired != Globals.DefaultIsRequired)
                        dataMemberAttribute.Arguments.Add(new CodeAttributeArgument(Globals.IsRequiredProperty, new CodePrimitiveExpression(dataMember.IsRequired)));
                    if (dataMember.EmitDefaultValue != Globals.DefaultEmitDefaultValue)
                        dataMemberAttribute.Arguments.Add(new CodeAttributeArgument(Globals.EmitDefaultValueProperty, new CodePrimitiveExpression(dataMember.EmitDefaultValue)));
                    if (dataMember.Order != Globals.DefaultOrder)
                        dataMemberAttribute.Arguments.Add(new CodeAttributeArgument(Globals.OrderProperty, new CodePrimitiveExpression(dataMember.Order)));
                    property.CustomAttributes.Add(dataMemberAttribute);

                    type.Members.Add(field);
                    type.Members.Add(property);
                }
            }
        }

        private bool CanDeclareAssemblyAttribute(ContractCodeDomInfo contractCodeDomInfo)
        {
            return SupportsAssemblyAttributes && !contractCodeDomInfo.UsesWildcardNamespace;
        }

        private bool NeedsExplicitNamespace(string dataContractNamespace, string clrNamespace)
        {
            return (DataContract.GetDefaultStableNamespace(clrNamespace) != dataContractNamespace);
        }

        internal ICollection<CodeTypeReference> GetKnownTypeReferences(DataContract dataContract)
        {
            DataContractDictionary knownTypeDictionary = GetKnownTypeContracts(dataContract);
            if (knownTypeDictionary == null)
                return null;

            ICollection<DataContract> knownTypeContracts = knownTypeDictionary.Values;
            if (knownTypeContracts == null || knownTypeContracts.Count == 0)
                return null;

            List<CodeTypeReference> knownTypeReferences = new List<CodeTypeReference>();
            foreach (DataContract knownTypeContract in knownTypeContracts)
            {
                knownTypeReferences.Add(GetCodeTypeReference(knownTypeContract));
            }
            return knownTypeReferences;
        }

        private DataContractDictionary GetKnownTypeContracts(DataContract dataContract)
        {
            if (_dataContractSet.KnownTypesForObject != null && SchemaImporter.IsObjectContract(dataContract))
            {
                return _dataContractSet.KnownTypesForObject;
            }
            else if (dataContract is ClassDataContract)
            {
                ContractCodeDomInfo contractCodeDomInfo = GetContractCodeDomInfo(dataContract);
                if (!contractCodeDomInfo.IsProcessed)
                    GenerateType(dataContract, contractCodeDomInfo);
                if (contractCodeDomInfo.ReferencedTypeExists)
                    return GetKnownTypeContracts((ClassDataContract)dataContract, new Dictionary<DataContract, object>());
            }
            return null;
        }

        private DataContractDictionary GetKnownTypeContracts(ClassDataContract dataContract, Dictionary<DataContract, object> handledContracts)
        {
            if (handledContracts.ContainsKey(dataContract))
                return dataContract.KnownDataContracts;

            handledContracts.Add(dataContract, null);
            if (dataContract.Members != null)
            {
                bool objectMemberHandled = false;
                foreach (DataMember dataMember in dataContract.Members)
                {
                    DataContract memberContract = dataMember.MemberTypeContract;
                    if (!objectMemberHandled && _dataContractSet.KnownTypesForObject != null && SchemaImporter.IsObjectContract(memberContract))
                    {
                        AddKnownTypeContracts(dataContract, _dataContractSet.KnownTypesForObject);
                        objectMemberHandled = true;
                    }
                    else if (memberContract is ClassDataContract)
                    {
                        ContractCodeDomInfo memberCodeDomInfo = GetContractCodeDomInfo(memberContract);
                        if (!memberCodeDomInfo.IsProcessed)
                            GenerateType(memberContract, memberCodeDomInfo);
                        if (memberCodeDomInfo.ReferencedTypeExists)
                        {
                            AddKnownTypeContracts(dataContract, GetKnownTypeContracts((ClassDataContract)memberContract, handledContracts));
                        }
                    }
                }
            }

            return dataContract.KnownDataContracts;
        }

        // [Fx.Tag.SecurityNote(Critical = "Sets critical properties on internal DataContract.",
        //     Safe = "Called during schema import/code generation.")]
        [SecuritySafeCritical]
        private void AddKnownTypeContracts(ClassDataContract dataContract, DataContractDictionary knownContracts)
        {
            if (knownContracts == null || knownContracts.Count == 0)
                return;

            if (dataContract.KnownDataContracts == null)
                dataContract.KnownDataContracts = new DataContractDictionary();

            foreach (KeyValuePair<XmlQualifiedName, DataContract> pair in knownContracts)
            {
                if (dataContract.StableName != pair.Key && !dataContract.KnownDataContracts.ContainsKey(pair.Key) && !pair.Value.IsBuiltInDataContract)
                    dataContract.KnownDataContracts.Add(pair.Key, pair.Value);
            }
        }

        private void AddKnownTypes(ClassDataContract dataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            DataContractDictionary knownContractDictionary = GetKnownTypeContracts(dataContract, new Dictionary<DataContract, object>());
            if (knownContractDictionary == null || knownContractDictionary.Count == 0)
                return;

            ICollection<DataContract> knownTypeContracts = knownContractDictionary.Values;
            foreach (DataContract knownTypeContract in knownTypeContracts)
            {
                CodeAttributeDeclaration knownTypeAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfKnownTypeAttribute));
                knownTypeAttribute.Arguments.Add(new CodeAttributeArgument(new CodeTypeOfExpression(GetCodeTypeReference(knownTypeContract))));
                contractCodeDomInfo.TypeDeclaration.CustomAttributes.Add(knownTypeAttribute);
            }
            AddImportStatement(Globals.TypeOfKnownTypeAttribute.Namespace, contractCodeDomInfo.CodeNamespace);
        }

        private CodeTypeReference WrapNullable(CodeTypeReference memberType)
        {
            if (!SupportsGenericTypeReference)
                return memberType;

            CodeTypeReference nullableOfMemberType = GetCodeTypeReference(Globals.TypeOfNullable);
            nullableOfMemberType.TypeArguments.Add(memberType);
            return nullableOfMemberType;
        }

        private void AddExtensionData(ContractCodeDomInfo contractCodeDomInfo)
        {
            if (contractCodeDomInfo != null && contractCodeDomInfo.TypeDeclaration != null)
            {
                CodeTypeDeclaration type = contractCodeDomInfo.TypeDeclaration;
                type.BaseTypes.Add(DataContract.GetClrTypeFullName(Globals.TypeOfIExtensibleDataObject));
                CodeMemberField extensionDataObjectField = ExtensionDataObjectField;

                type.Members.Add(extensionDataObjectField);
                contractCodeDomInfo.GetMemberNames().Add(extensionDataObjectField.Name, null);
                CodeMemberProperty extensionDataObjectProperty = ExtensionDataObjectProperty;
                type.Members.Add(extensionDataObjectProperty);
                contractCodeDomInfo.GetMemberNames().Add(extensionDataObjectProperty.Name, null);
            }
        }

        private void AddPropertyChangedNotifier(ContractCodeDomInfo contractCodeDomInfo, bool isValueType)
        {
            if (EnableDataBinding && SupportsDeclareEvents && contractCodeDomInfo != null && contractCodeDomInfo.TypeDeclaration != null)
            {
                CodeTypeDeclaration codeTypeDeclaration = contractCodeDomInfo.TypeDeclaration;
                codeTypeDeclaration.BaseTypes.Add(CodeTypeIPropertyChange);
                CodeMemberEvent memberEvent = PropertyChangedEvent;
                codeTypeDeclaration.Members.Add(memberEvent);
                CodeMemberMethod raisePropertyChangedEventMethod = RaisePropertyChangedEventMethod;
                if (!isValueType)
                    raisePropertyChangedEventMethod.Attributes |= MemberAttributes.Family;
                codeTypeDeclaration.Members.Add(raisePropertyChangedEventMethod);
                contractCodeDomInfo.GetMemberNames().Add(memberEvent.Name, null);
                contractCodeDomInfo.GetMemberNames().Add(raisePropertyChangedEventMethod.Name, null);
            }
        }

        private void ThrowIfReferencedBaseTypeSealed(Type baseType, DataContract dataContract)
        {
            if (baseType.GetTypeInfo().IsSealed)
                throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.CannotDeriveFromSealedReferenceType, dataContract.StableName.Name, dataContract.StableName.Namespace, DataContract.GetClrTypeFullName(baseType))));
        }

        private void ExportEnumDataContract(EnumDataContract enumDataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            GenerateType(enumDataContract, contractCodeDomInfo);
            if (contractCodeDomInfo.ReferencedTypeExists)
                return;

            CodeTypeDeclaration type = contractCodeDomInfo.TypeDeclaration;
            type.IsEnum = true;
            type.BaseTypes.Add(EnumDataContract.GetBaseType(enumDataContract.BaseContractName));
            if (enumDataContract.IsFlags)
            {
                type.CustomAttributes.Add(new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfFlagsAttribute)));
                AddImportStatement(Globals.TypeOfFlagsAttribute.Namespace, contractCodeDomInfo.CodeNamespace);
            }

            string dataContractName = GetNameForAttribute(enumDataContract.StableName.Name);
            CodeAttributeDeclaration dataContractAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfDataContractAttribute));
            dataContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NameProperty, new CodePrimitiveExpression(dataContractName)));
            dataContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NamespaceProperty, new CodePrimitiveExpression(enumDataContract.StableName.Namespace)));
            type.CustomAttributes.Add(dataContractAttribute);
            AddImportStatement(Globals.TypeOfDataContractAttribute.Namespace, contractCodeDomInfo.CodeNamespace);

            if (enumDataContract.Members != null)
            {
                for (int i = 0; i < enumDataContract.Members.Count; i++)
                {
                    string stringValue = enumDataContract.Members[i].Name;
                    long longValue = enumDataContract.Values[i];

                    CodeMemberField enumMember = new CodeMemberField();
                    if (enumDataContract.IsULong)
                        enumMember.InitExpression = new CodeSnippetExpression(enumDataContract.GetStringFromEnumValue(longValue));
                    else
                        enumMember.InitExpression = new CodePrimitiveExpression(longValue);
                    enumMember.Name = GetMemberName(stringValue, contractCodeDomInfo);
                    CodeAttributeDeclaration enumMemberAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfEnumMemberAttribute));
                    if (enumMember.Name != stringValue)
                        enumMemberAttribute.Arguments.Add(new CodeAttributeArgument(Globals.ValueProperty, new CodePrimitiveExpression(stringValue)));
                    enumMember.CustomAttributes.Add(enumMemberAttribute);
                    type.Members.Add(enumMember);
                }
            }
        }

        private void GenerateKeyValueType(ClassDataContract keyValueContract)
        {
            // Add code for KeyValue item type in the case where its usage is limited to dictionary 
            // and dictionary is not found in referenced types
            if (keyValueContract != null && _dataContractSet[keyValueContract.StableName] == null)
            {
                ContractCodeDomInfo contractCodeDomInfo = _dataContractSet.GetContractCodeDomInfo(keyValueContract);
                if (contractCodeDomInfo == null)
                {
                    contractCodeDomInfo = new ContractCodeDomInfo();
                    _dataContractSet.SetContractCodeDomInfo(keyValueContract, contractCodeDomInfo);
                    ExportClassDataContract(keyValueContract, contractCodeDomInfo);
                    contractCodeDomInfo.IsProcessed = true;
                }
            }
        }

        private void ExportCollectionDataContract(CollectionDataContract collectionContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            GenerateType(collectionContract, contractCodeDomInfo);
            if (contractCodeDomInfo.ReferencedTypeExists)
                return;

            string dataContractName = GetNameForAttribute(collectionContract.StableName.Name);

            // If type name is not expected, generate collection type that derives from referenced list type and uses [CollectionDataContract] 
            if (!SupportsGenericTypeReference)
                throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(
                    string.Format(SRSerialization.CannotUseGenericTypeAsBase, dataContractName,
                    collectionContract.StableName.Namespace)));

            DataContract itemContract = collectionContract.ItemContract;
            bool isItemTypeNullable = collectionContract.IsItemTypeNullable;

            CodeTypeReference baseTypeReference;
            bool foundDictionaryBase = TryGetReferencedDictionaryType(collectionContract, out baseTypeReference);
            if (!foundDictionaryBase)
            {
                if (collectionContract.IsDictionary)
                {
                    GenerateKeyValueType(collectionContract.ItemContract as ClassDataContract);
                }
                if (!TryGetReferencedListType(itemContract, isItemTypeNullable, out baseTypeReference))
                {
                    if (SupportsGenericTypeReference)
                    {
                        baseTypeReference = GetCodeTypeReference(Globals.TypeOfListGeneric);
                        baseTypeReference.TypeArguments.Add(GetElementTypeReference(itemContract, isItemTypeNullable));
                    }
                    else
                    {
                        string expectedTypeName = Globals.ArrayPrefix + itemContract.StableName.Name;
                        string expectedTypeNs = DataContract.GetCollectionNamespace(itemContract.StableName.Namespace);
                        throw /*System.Runtime.Serialization.*/DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(string.Format(SRSerialization.ReferencedBaseTypeDoesNotExist,
                            dataContractName, collectionContract.StableName.Namespace,
                            expectedTypeName, expectedTypeNs, DataContract.GetClrTypeFullName(Globals.TypeOfIListGeneric), DataContract.GetClrTypeFullName(Globals.TypeOfICollectionGeneric))));
                    }
                }
            }

            CodeTypeDeclaration generatedType = contractCodeDomInfo.TypeDeclaration;
            generatedType.BaseTypes.Add(baseTypeReference);
            CodeAttributeDeclaration collectionContractAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfCollectionDataContractAttribute));
            collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NameProperty, new CodePrimitiveExpression(dataContractName)));
            collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.NamespaceProperty, new CodePrimitiveExpression(collectionContract.StableName.Namespace)));
            if (collectionContract.IsReference != Globals.DefaultIsReference)
                collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.IsReferenceProperty, new CodePrimitiveExpression(collectionContract.IsReference)));
            collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.ItemNameProperty, new CodePrimitiveExpression(GetNameForAttribute(collectionContract.ItemName))));
            if (foundDictionaryBase)
            {
                collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.KeyNameProperty, new CodePrimitiveExpression(GetNameForAttribute(collectionContract.KeyName))));
                collectionContractAttribute.Arguments.Add(new CodeAttributeArgument(Globals.ValueNameProperty, new CodePrimitiveExpression(GetNameForAttribute(collectionContract.ValueName))));
            }
            generatedType.CustomAttributes.Add(collectionContractAttribute);
            AddImportStatement(Globals.TypeOfCollectionDataContractAttribute.Namespace, contractCodeDomInfo.CodeNamespace);
            AddSerializableAttribute(GenerateSerializableTypes, generatedType, contractCodeDomInfo);
        }

        private void ExportXmlDataContract(XmlDataContract xmlDataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            GenerateType(xmlDataContract, contractCodeDomInfo);
            if (contractCodeDomInfo.ReferencedTypeExists)
                return;

            CodeTypeDeclaration type = contractCodeDomInfo.TypeDeclaration;
            if (SupportsPartialTypes)
                type.IsPartial = true;
            if (xmlDataContract.IsValueType)
                type.IsStruct = true;
            else
            {
                type.IsClass = true;
                type.BaseTypes.Add(Globals.TypeOfObject);
            }
            AddSerializableAttribute(GenerateSerializableTypes, type, contractCodeDomInfo);

            type.BaseTypes.Add(DataContract.GetClrTypeFullName(Globals.TypeOfIXmlSerializable));

            type.Members.Add(NodeArrayField);
            type.Members.Add(NodeArrayProperty);
            type.Members.Add(ReadXmlMethod);
            type.Members.Add(WriteXmlMethod);
            type.Members.Add(GetSchemaMethod);
            if (xmlDataContract.IsAnonymous && !xmlDataContract.HasRoot)
            {
                type.CustomAttributes.Add(new CodeAttributeDeclaration(
                    DataContract.GetClrTypeFullName(Globals.TypeOfXmlSchemaProviderAttribute),
                    new CodeAttributeArgument(NullReference),
                    new CodeAttributeArgument(Globals.IsAnyProperty, new CodePrimitiveExpression(true)))
                );
            }
            else
            {
                type.CustomAttributes.Add(new CodeAttributeDeclaration(
                    DataContract.GetClrTypeFullName(Globals.TypeOfXmlSchemaProviderAttribute),
                    new CodeAttributeArgument(new CodePrimitiveExpression(Globals.ExportSchemaMethod)))
                );

                CodeMemberField typeNameField = new CodeMemberField(Globals.TypeOfXmlQualifiedName, s_typeNameFieldName);
                typeNameField.Attributes |= MemberAttributes.Static | MemberAttributes.Private;
                XmlQualifiedName typeName = xmlDataContract.IsAnonymous
                    ? SchemaImporter.ImportActualType(xmlDataContract.XsdType.Annotation, xmlDataContract.StableName, xmlDataContract.StableName)
                    : xmlDataContract.StableName;
                typeNameField.InitExpression = new CodeObjectCreateExpression(Globals.TypeOfXmlQualifiedName, new CodePrimitiveExpression(typeName.Name), new CodePrimitiveExpression(typeName.Namespace));
                type.Members.Add(typeNameField);

                type.Members.Add(GetSchemaStaticMethod);

                bool isElementNameDifferent =
                    (xmlDataContract.TopLevelElementName != null && xmlDataContract.TopLevelElementName.Value != xmlDataContract.StableName.Name) ||
                    (xmlDataContract.TopLevelElementNamespace != null && xmlDataContract.TopLevelElementNamespace.Value != xmlDataContract.StableName.Namespace);
                if (isElementNameDifferent || xmlDataContract.IsTopLevelElementNullable == false)
                {
                    CodeAttributeDeclaration xmlRootAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfXmlRootAttribute));
                    if (isElementNameDifferent)
                    {
                        if (xmlDataContract.TopLevelElementName != null)
                        {
                            xmlRootAttribute.Arguments.Add(new CodeAttributeArgument("ElementName", new CodePrimitiveExpression(xmlDataContract.TopLevelElementName.Value)));
                        }
                        if (xmlDataContract.TopLevelElementNamespace != null)
                        {
                            xmlRootAttribute.Arguments.Add(new CodeAttributeArgument("Namespace", new CodePrimitiveExpression(xmlDataContract.TopLevelElementNamespace.Value)));
                        }
                    }
                    if (xmlDataContract.IsTopLevelElementNullable == false)
                        xmlRootAttribute.Arguments.Add(new CodeAttributeArgument("IsNullable", new CodePrimitiveExpression(false)));
                    type.CustomAttributes.Add(xmlRootAttribute);
                }
            }
            AddPropertyChangedNotifier(contractCodeDomInfo, type.IsStruct);
        }

        private CodeNamespace GetCodeNamespace(string clrNamespace, string dataContractNamespace, ContractCodeDomInfo contractCodeDomInfo)
        {
            if (contractCodeDomInfo.CodeNamespace != null)
                return contractCodeDomInfo.CodeNamespace;

            CodeNamespaceCollection codeNamespaceCollection = _codeCompileUnit.Namespaces;
            foreach (CodeNamespace ns in codeNamespaceCollection)
            {
                if (ns.Name == clrNamespace)
                {
                    contractCodeDomInfo.CodeNamespace = ns;
                    return ns;
                }
            }

            CodeNamespace codeNamespace = new CodeNamespace(clrNamespace);
            codeNamespaceCollection.Add(codeNamespace);

            if (CanDeclareAssemblyAttribute(contractCodeDomInfo)
                && NeedsExplicitNamespace(dataContractNamespace, clrNamespace))
            {
                CodeAttributeDeclaration namespaceAttribute = new CodeAttributeDeclaration(DataContract.GetClrTypeFullName(Globals.TypeOfContractNamespaceAttribute));
                namespaceAttribute.Arguments.Add(new CodeAttributeArgument(new CodePrimitiveExpression(dataContractNamespace)));
                namespaceAttribute.Arguments.Add(new CodeAttributeArgument(Globals.ClrNamespaceProperty, new CodePrimitiveExpression(clrNamespace)));
                _codeCompileUnit.AssemblyCustomAttributes.Add(namespaceAttribute);
            }
            contractCodeDomInfo.CodeNamespace = codeNamespace;
            return codeNamespace;
        }

        private string GetMemberName(string memberName, ContractCodeDomInfo contractCodeDomInfo)
        {
            memberName = GetClrIdentifier(memberName, Globals.DefaultGeneratedMember);

            if (memberName == contractCodeDomInfo.TypeDeclaration.Name)
                memberName = AppendToValidClrIdentifier(memberName, Globals.DefaultMemberSuffix);

            if (contractCodeDomInfo.GetMemberNames().ContainsKey(memberName))
            {
                string uniqueMemberName = null;
                for (int i = 1; ; i++)
                {
                    uniqueMemberName = AppendToValidClrIdentifier(memberName, i.ToString(NumberFormatInfo.InvariantInfo));
                    if (!contractCodeDomInfo.GetMemberNames().ContainsKey(uniqueMemberName))
                    {
                        memberName = uniqueMemberName;
                        break;
                    }
                }
            }

            contractCodeDomInfo.GetMemberNames().Add(memberName, null);
            return memberName;
        }

        private void AddBaseMemberNames(ContractCodeDomInfo baseContractCodeDomInfo, ContractCodeDomInfo contractCodeDomInfo)
        {
            if (!baseContractCodeDomInfo.ReferencedTypeExists)
            {
                Dictionary<string, object> baseMemberNames = baseContractCodeDomInfo.GetMemberNames();
                Dictionary<string, object> memberNames = contractCodeDomInfo.GetMemberNames();
                foreach (KeyValuePair<string, object> pair in baseMemberNames)
                {
                    memberNames.Add(pair.Key, pair.Value);
                }
            }
        }

        // [Fx.Tag.SecurityNote(Critical = "Critical because it calls the CodeGenerator.IsValidLanguageIndependentIdentifier(..) method that has a LinkDemand.",
        //     Safe = "Safe because it doesn't leak security sensitive information.")]
        [SecuritySafeCritical]
        private static string GetClrIdentifier(string identifier, string defaultIdentifier)
        {
            if (identifier.Length <= MaxIdentifierLength && Microsoft.CodeDom.Compiler.CodeGenerator.IsValidLanguageIndependentIdentifier(identifier))
                return identifier;

            bool isStart = true;
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < identifier.Length && builder.Length < MaxIdentifierLength; i++)
            {
                char c = identifier[i];
                if (IsValid(c))
                {
                    if (isStart && !IsValidStart(c))
                        builder.Append("_");
                    builder.Append(c);
                    isStart = false;
                }
            }
            if (builder.Length == 0)
                return defaultIdentifier;

            return builder.ToString();
        }

        private static string AppendToValidClrIdentifier(string identifier, string appendString)
        {
            int availableLength = MaxIdentifierLength - identifier.Length;
            int requiredLength = appendString.Length;
            if (availableLength < requiredLength)
                identifier = identifier.Substring(0, MaxIdentifierLength - requiredLength);
            identifier += appendString;
            return identifier;
        }

        private string GetClrNamespace(DataContract dataContract, ContractCodeDomInfo contractCodeDomInfo)
        {
            string clrNamespace = contractCodeDomInfo.ClrNamespace;
            bool usesWildcardNamespace = false;
            if (clrNamespace == null)
            {
                if (!Namespaces.TryGetValue(dataContract.StableName.Namespace, out clrNamespace))
                {
                    if (Namespaces.TryGetValue(s_wildcardNamespaceMapping, out clrNamespace))
                    {
                        usesWildcardNamespace = true;
                    }
                    else
                    {
                        clrNamespace = GetClrNamespace(dataContract.StableName.Namespace);
                        if (ClrNamespaces.ContainsKey(clrNamespace))
                        {
                            string uniqueNamespace = null;
                            for (int i = 1; ; i++)
                            {
                                uniqueNamespace = ((clrNamespace.Length == 0) ? Globals.DefaultClrNamespace : clrNamespace) + i.ToString(NumberFormatInfo.InvariantInfo);
                                if (!ClrNamespaces.ContainsKey(uniqueNamespace))
                                {
                                    clrNamespace = uniqueNamespace;
                                    break;
                                }
                            }
                        }
                        AddNamespacePair(dataContract.StableName.Namespace, clrNamespace);
                    }
                }
                contractCodeDomInfo.ClrNamespace = clrNamespace;
                contractCodeDomInfo.UsesWildcardNamespace = usesWildcardNamespace;
            }
            return clrNamespace;
        }

        private void AddNamespacePair(string dataContractNamespace, string clrNamespace)
        {
            Namespaces.Add(dataContractNamespace, clrNamespace);
            ClrNamespaces.Add(clrNamespace, dataContractNamespace);
        }

        private void AddImportStatement(string clrNamespace, CodeNamespace codeNamespace)
        {
            if (clrNamespace == codeNamespace.Name)
                return;

            CodeNamespaceImportCollection importCollection = codeNamespace.Imports;
            foreach (CodeNamespaceImport import in importCollection)
            {
                if (import.Namespace == clrNamespace)
                    return;
            }

            importCollection.Add(new CodeNamespaceImport(clrNamespace));
        }

        private static string GetClrNamespace(string dataContractNamespace)
        {
            if (dataContractNamespace == null || dataContractNamespace.Length == 0)
                return String.Empty;

            Uri uri = null;
            StringBuilder builder = new StringBuilder();
            if (Uri.TryCreate(dataContractNamespace, UriKind.RelativeOrAbsolute, out uri))
            {
                Dictionary<string, object> fragments = new Dictionary<string, object>(StringComparer.Ordinal);
                if (!uri.IsAbsoluteUri)
                    AddToNamespace(builder, uri.OriginalString, fragments);
                else
                {
                    string uriString = uri.AbsoluteUri;
                    if (uriString.StartsWith(Globals.DataContractXsdBaseNamespace, StringComparison.Ordinal))
                        AddToNamespace(builder, uriString.Substring(Globals.DataContractXsdBaseNamespace.Length), fragments);
                    else
                    {
                        string host = uri.Host;
                        if (host != null)
                            AddToNamespace(builder, host, fragments);
                        string path = uri.PathAndQuery;
                        if (path != null)
                            AddToNamespace(builder, path, fragments);
                    }
                }
            }

            if (builder.Length == 0)
                return String.Empty;

            int length = builder.Length;
            if (builder[builder.Length - 1] == '.')
                length--;
            length = Math.Min(MaxIdentifierLength, length);

            return builder.ToString(0, length);
        }

        private static void AddToNamespace(StringBuilder builder, string fragment, Dictionary<string, object> fragments)
        {
            if (fragment == null)
                return;
            bool isStart = true;
            int fragmentOffset = builder.Length;
            int fragmentLength = 0;

            for (int i = 0; i < fragment.Length && builder.Length < MaxIdentifierLength; i++)
            {
                char c = fragment[i];

                if (IsValid(c))
                {
                    if (isStart && !IsValidStart(c))
                        builder.Append("_");
                    builder.Append(c);
                    fragmentLength++;
                    isStart = false;
                }
                else if ((c == '.' || c == '/' || c == ':') && (builder.Length == 1
                    || (builder.Length > 1 && builder[builder.Length - 1] != '.')))
                {
                    AddNamespaceFragment(builder, fragmentOffset, fragmentLength, fragments);
                    builder.Append('.');
                    fragmentOffset = builder.Length;
                    fragmentLength = 0;
                    isStart = true;
                }
            }
            AddNamespaceFragment(builder, fragmentOffset, fragmentLength, fragments);
        }

        private static void AddNamespaceFragment(StringBuilder builder, int fragmentOffset,
            int fragmentLength, Dictionary<string, object> fragments)
        {
            if (fragmentLength == 0)
                return;

            string nsFragment = builder.ToString(fragmentOffset, fragmentLength);
            if (fragments.ContainsKey(nsFragment))
            {
                for (int i = 1; ; i++)
                {
                    string uniquifier = i.ToString(NumberFormatInfo.InvariantInfo);
                    string uniqueNsFragment = AppendToValidClrIdentifier(nsFragment, uniquifier);
                    if (!fragments.ContainsKey(uniqueNsFragment))
                    {
                        builder.Append(uniquifier);
                        nsFragment = uniqueNsFragment;
                        break;
                    }
                    if (i == Int32.MaxValue)
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataContractException(string.Format(SRSerialization.CannotComputeUniqueName, nsFragment)));
                }
            }
            fragments.Add(nsFragment, null);
        }

        private static bool IsValidStart(char c)
        {
            return (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.DecimalDigitNumber);
        }

        private static bool IsValid(char c)
        {
            UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(c);

            // each char must be Lu, Ll, Lt, Lm, Lo, Nd, Mn, Mc, Pc

            switch (uc)
            {
                case UnicodeCategory.UppercaseLetter:        // Lu
                case UnicodeCategory.LowercaseLetter:        // Ll
                case UnicodeCategory.TitlecaseLetter:        // Lt
                case UnicodeCategory.ModifierLetter:         // Lm
                case UnicodeCategory.OtherLetter:            // Lo
                case UnicodeCategory.DecimalDigitNumber:     // Nd
                case UnicodeCategory.NonSpacingMark:         // Mn
                case UnicodeCategory.SpacingCombiningMark:   // Mc
                case UnicodeCategory.ConnectorPunctuation:   // Pc
                    return true;
                default:
                    return false;
            }
        }

        private CodeTypeReference CodeTypeIPropertyChange
        {
            get { return GetCodeTypeReference(typeof(System.ComponentModel.INotifyPropertyChanged)); }
        }

        private CodeThisReferenceExpression ThisReference
        {
            get { return new CodeThisReferenceExpression(); }
        }

        private CodePrimitiveExpression NullReference
        {
            get { return new CodePrimitiveExpression(null); }
        }
        private CodeMemberProperty NodeArrayProperty
        {
            get
            {
                return CreateProperty(GetCodeTypeReference(Globals.TypeOfXmlNodeArray), Globals.NodeArrayPropertyName, Globals.NodeArrayFieldName, false/*isValueType*/);
            }
        }

        private CodeMemberField NodeArrayField
        {
            get
            {
                CodeMemberField nodeArrayField = new CodeMemberField();
                nodeArrayField.Type = GetCodeTypeReference(Globals.TypeOfXmlNodeArray);
                nodeArrayField.Name = Globals.NodeArrayFieldName;
                nodeArrayField.Attributes = MemberAttributes.Private;
                return nodeArrayField;
            }
        }
        private CodeMemberMethod ReadXmlMethod
        {
            get
            {
                CodeMemberMethod readXmlMethod = new CodeMemberMethod();
                readXmlMethod.Name = "ReadXml";
                CodeParameterDeclarationExpression readerArg = new CodeParameterDeclarationExpression(typeof(XmlReader), "reader");
                readXmlMethod.Parameters.Add(readerArg);
                readXmlMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
                readXmlMethod.ImplementationTypes.Add(Globals.TypeOfIXmlSerializable);
                CodeAssignStatement setNode = new CodeAssignStatement();
                setNode.Left = new CodeFieldReferenceExpression(ThisReference, Globals.NodeArrayFieldName);
                setNode.Right = new CodeMethodInvokeExpression(
                                      new CodeTypeReferenceExpression(GetCodeTypeReference(Globals.TypeOfXmlSerializableServices)),
                                      XmlSerializableServices.ReadNodesMethodName,
                                      new CodeArgumentReferenceExpression(readerArg.Name)
                                    );
                readXmlMethod.Statements.Add(setNode);
                return readXmlMethod;
            }
        }

        private CodeMemberMethod WriteXmlMethod
        {
            get
            {
                CodeMemberMethod writeXmlMethod = new CodeMemberMethod();
                writeXmlMethod.Name = "WriteXml";
                CodeParameterDeclarationExpression writerArg = new CodeParameterDeclarationExpression(typeof(XmlWriter), "writer");
                writeXmlMethod.Parameters.Add(writerArg);
                writeXmlMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
                writeXmlMethod.ImplementationTypes.Add(Globals.TypeOfIXmlSerializable);
                writeXmlMethod.Statements.Add(
                    new CodeMethodInvokeExpression(
                        new CodeTypeReferenceExpression(GetCodeTypeReference(Globals.TypeOfXmlSerializableServices)),
                        XmlSerializableServices.WriteNodesMethodName,
                        new CodeArgumentReferenceExpression(writerArg.Name),
                        new CodePropertyReferenceExpression(ThisReference, Globals.NodeArrayPropertyName)
                    )
                );
                return writeXmlMethod;
            }
        }

        private CodeMemberMethod GetSchemaMethod
        {
            get
            {
                CodeMemberMethod getSchemaMethod = new CodeMemberMethod();
                getSchemaMethod.Name = "GetSchema";
                getSchemaMethod.Attributes = MemberAttributes.Public | MemberAttributes.Final;
                getSchemaMethod.ImplementationTypes.Add(Globals.TypeOfIXmlSerializable);
                getSchemaMethod.ReturnType = GetCodeTypeReference(typeof(XmlSchema));
                getSchemaMethod.Statements.Add(new CodeMethodReturnStatement(NullReference));
                return getSchemaMethod;
            }
        }

        private CodeMemberMethod GetSchemaStaticMethod
        {
            get
            {
                CodeMemberMethod getSchemaStaticMethod = new CodeMemberMethod();
                getSchemaStaticMethod.Name = Globals.ExportSchemaMethod;
                getSchemaStaticMethod.ReturnType = GetCodeTypeReference(Globals.TypeOfXmlQualifiedName);
                CodeParameterDeclarationExpression paramDeclaration = new CodeParameterDeclarationExpression(Globals.TypeOfXmlSchemaSet, "schemas");
                getSchemaStaticMethod.Parameters.Add(paramDeclaration);
                getSchemaStaticMethod.Attributes = MemberAttributes.Static | MemberAttributes.Public;
                getSchemaStaticMethod.Statements.Add(
                    new CodeMethodInvokeExpression(
                        new CodeTypeReferenceExpression(GetCodeTypeReference(typeof(XmlSerializableServices))),
                        XmlSerializableServices.AddDefaultSchemaMethodName,
                        new CodeArgumentReferenceExpression(paramDeclaration.Name),
                        new CodeFieldReferenceExpression(null, s_typeNameFieldName)
                    )
                );
                getSchemaStaticMethod.Statements.Add(
                    new CodeMethodReturnStatement(
                        new CodeFieldReferenceExpression(null, s_typeNameFieldName)
                    )
                );
                return getSchemaStaticMethod;
            }
        }

        private CodeMemberField ExtensionDataObjectField
        {
            get
            {
                CodeMemberField extensionDataObjectField = new CodeMemberField();
                extensionDataObjectField.Type = GetCodeTypeReference(Globals.TypeOfExtensionDataObject);
                extensionDataObjectField.Name = Globals.ExtensionDataObjectFieldName;
                extensionDataObjectField.Attributes = MemberAttributes.Private;
                return extensionDataObjectField;
            }
        }

        private CodeMemberProperty ExtensionDataObjectProperty
        {
            get
            {
                CodeMemberProperty extensionDataObjectProperty = new CodeMemberProperty();
                extensionDataObjectProperty.Type = GetCodeTypeReference(Globals.TypeOfExtensionDataObject);
                extensionDataObjectProperty.Name = Globals.ExtensionDataObjectPropertyName;
                extensionDataObjectProperty.Attributes = MemberAttributes.Public | MemberAttributes.Final;
                extensionDataObjectProperty.ImplementationTypes.Add(Globals.TypeOfIExtensibleDataObject);

                CodeMethodReturnStatement propertyGet = new CodeMethodReturnStatement();
                propertyGet.Expression = new CodeFieldReferenceExpression(ThisReference, Globals.ExtensionDataObjectFieldName);
                extensionDataObjectProperty.GetStatements.Add(propertyGet);

                CodeAssignStatement propertySet = new CodeAssignStatement();
                propertySet.Left = new CodeFieldReferenceExpression(ThisReference, Globals.ExtensionDataObjectFieldName);
                propertySet.Right = new CodePropertySetValueReferenceExpression();
                extensionDataObjectProperty.SetStatements.Add(propertySet);

                return extensionDataObjectProperty;
            }
        }

        private CodeMemberMethod RaisePropertyChangedEventMethod
        {
            get
            {
                CodeMemberMethod raisePropertyChangedEventMethod = new CodeMemberMethod();
                raisePropertyChangedEventMethod.Name = "RaisePropertyChanged";
                raisePropertyChangedEventMethod.Attributes = MemberAttributes.Final;
                CodeArgumentReferenceExpression propertyName = new CodeArgumentReferenceExpression("propertyName");
                raisePropertyChangedEventMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string), propertyName.ParameterName));
                CodeVariableReferenceExpression propertyChanged = new CodeVariableReferenceExpression("propertyChanged");
                raisePropertyChangedEventMethod.Statements.Add(new CodeVariableDeclarationStatement(typeof(PropertyChangedEventHandler), propertyChanged.VariableName, new CodeEventReferenceExpression(ThisReference, PropertyChangedEvent.Name)));
                CodeConditionStatement ifStatement = new CodeConditionStatement(new CodeBinaryOperatorExpression(propertyChanged, CodeBinaryOperatorType.IdentityInequality, NullReference));
                raisePropertyChangedEventMethod.Statements.Add(ifStatement);
                ifStatement.TrueStatements.Add(new CodeDelegateInvokeExpression(propertyChanged, ThisReference, new CodeObjectCreateExpression(typeof(PropertyChangedEventArgs), propertyName)));
                return raisePropertyChangedEventMethod;
            }
        }

        private CodeMemberEvent PropertyChangedEvent
        {
            get
            {
                CodeMemberEvent propertyChangedEvent = new CodeMemberEvent();
                propertyChangedEvent.Attributes = MemberAttributes.Public;
                propertyChangedEvent.Name = "PropertyChanged";
                propertyChangedEvent.Type = GetCodeTypeReference(typeof(PropertyChangedEventHandler));
                propertyChangedEvent.ImplementationTypes.Add(Globals.TypeOfIPropertyChange);
                return propertyChangedEvent;
            }
        }

        private CodeMemberProperty CreateProperty(CodeTypeReference type, string propertyName, string fieldName, bool isValueType)
        {
            return CreateProperty(type, propertyName, fieldName, isValueType, EnableDataBinding && SupportsDeclareEvents);
        }

        private CodeMemberProperty CreateProperty(CodeTypeReference type, string propertyName, string fieldName, bool isValueType, bool raisePropertyChanged)
        {
            CodeMemberProperty property = new CodeMemberProperty();
            property.Type = type;
            property.Name = propertyName;
            property.Attributes = MemberAttributes.Final;
            if (GenerateInternalTypes)
                property.Attributes |= MemberAttributes.Assembly;
            else
                property.Attributes |= MemberAttributes.Public;

            CodeMethodReturnStatement propertyGet = new CodeMethodReturnStatement();
            propertyGet.Expression = new CodeFieldReferenceExpression(ThisReference, fieldName);
            property.GetStatements.Add(propertyGet);

            CodeAssignStatement propertySet = new CodeAssignStatement();
            propertySet.Left = new CodeFieldReferenceExpression(ThisReference, fieldName);
            propertySet.Right = new CodePropertySetValueReferenceExpression();
            if (raisePropertyChanged)
            {
                CodeConditionStatement ifStatement = new CodeConditionStatement();
                CodeExpression left = new CodeFieldReferenceExpression(ThisReference, fieldName);
                CodeExpression right = new CodePropertySetValueReferenceExpression();
                if (!isValueType)
                {
                    left = new CodeMethodInvokeExpression(new CodeTypeReferenceExpression(Globals.TypeOfObject),
                        "ReferenceEquals", new CodeExpression[] { left, right });
                }
                else
                {
                    left = new CodeMethodInvokeExpression(left, "Equals", new CodeExpression[] { right });
                }
                right = new CodePrimitiveExpression(true);
                ifStatement.Condition = new CodeBinaryOperatorExpression(left, CodeBinaryOperatorType.IdentityInequality, right);
                ifStatement.TrueStatements.Add(propertySet);
                ifStatement.TrueStatements.Add(new CodeMethodInvokeExpression(ThisReference, RaisePropertyChangedEventMethod.Name, new CodePrimitiveExpression(propertyName)));
                property.SetStatements.Add(ifStatement);
            }
            else
                property.SetStatements.Add(propertySet);
            return property;
        }
    }
}
