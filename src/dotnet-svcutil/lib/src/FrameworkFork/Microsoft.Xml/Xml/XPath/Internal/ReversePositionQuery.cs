// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MS.Internal.Xml.XPath
{
    using System;
    using Microsoft.Xml;
    using Microsoft.Xml.XPath;
    using System.Diagnostics;

    internal sealed class ReversePositionQuery : ForwardPositionQuery
    {
        public ReversePositionQuery(Query input) : base(input) { }
        private ReversePositionQuery(ReversePositionQuery other) : base(other) { }

        public override XPathNodeIterator Clone() { return new ReversePositionQuery(this); }
        public override int CurrentPosition { get { return outputBuffer.Count - count + 1; } }
        public override QueryProps Properties { get { return base.Properties | QueryProps.Reverse; } }
    }
}





