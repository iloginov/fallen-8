// 
// Fallen8.cs
//  
// Author:
//       Henning Rauch <Henning@RauchEntwicklung.biz>
// 
// Copyright (c) 2012 Henning Rauch
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#region Usings

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NoSQL.GraphDB.Algorithms.Path;
using NoSQL.GraphDB.Error;
using NoSQL.GraphDB.Expression;
using NoSQL.GraphDB.Helper;
using NoSQL.GraphDB.Index;
using NoSQL.GraphDB.Index.Fulltext;
using NoSQL.GraphDB.Index.Range;
using NoSQL.GraphDB.Index.Spatial;
using NoSQL.GraphDB.Log;
using NoSQL.GraphDB.Model;
using NoSQL.GraphDB.Persistency;
using NoSQL.GraphDB.Plugin;
using NoSQL.GraphDB.Service;

#endregion

namespace NoSQL.GraphDB
{
    /// <summary>
    ///   Fallen8.
    /// </summary>
    public sealed class Fallen8 : AThreadSafeElement, IRead, IWrite, IDisposable
    {
        #region Data

        /// <summary>
        ///   The graph elements
        /// </summary>
        private BigList<AGraphElement> _graphElements;

        /// <summary>
        ///   The index factory.
        /// </summary>
        public IndexFactory IndexFactory { get; internal set; }

        /// <summary>
        ///   The index factory.
        /// </summary>
        public ServiceFactory ServiceFactory { get; internal set; }

        /// <summary>
        /// The count of edges
        /// </summary>
        public UInt32 EdgeCount { get; private set; }

        /// <summary>
        /// The count of vertices
        /// </summary>
        public UInt32 VertexCount { get; private set; }

        /// <summary>
        ///   The current identifier.
        /// </summary>
        private Int32 _currentId = 0;

        /// <summary>
        ///   Binary operator delegate.
        /// </summary>
        private delegate Boolean BinaryOperatorDelegate(IComparable property, IComparable literal);

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class.
        /// </summary>
        public Fallen8()
        {
            IndexFactory = new IndexFactory();
            _graphElements = new BigList<AGraphElement>();
            ServiceFactory = new ServiceFactory(this);
            IndexFactory.Indices.Clear();
        }

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class and loads the vertices from a save point.
        /// </summary>
        /// <param name='path'> Path to the save point. </param>
        public Fallen8(String path)
            :this()
        {
            Load(path, true);
        }

        #endregion

        #region IWrite implementation

        public void Load(String path, Boolean startServices = false)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                Logger.LogInfo(String.Format("There is no path given, so nothing will be loaded."));
                return;
            }

            Logger.LogInfo(String.Format("Fallen-8 now loads a savegame from path \"{0}\"", path));

            if (ReadResource())
            {
                var oldIndexFactory = IndexFactory;
                var oldServiceFactory = ServiceFactory;
                oldServiceFactory.ShutdownAllServices();
                var oldGraphElements = _graphElements;
#if __MonoCS__
    //mono specific code
				#else
                GC.Collect();
                GC.Collect();
                GC.WaitForFullGCComplete();
                GC.WaitForPendingFinalizers();
#endif
                _graphElements = new BigList<AGraphElement>();

                var success = PersistencyFactory.Load(this, ref _graphElements, path, ref _currentId, startServices);

                if (success)
                {
                    oldIndexFactory.DeleteAllIndices();
                }
                else
                {
                    _graphElements = oldGraphElements;
                    IndexFactory = oldIndexFactory;
                    ServiceFactory = oldServiceFactory;
                    ServiceFactory.StartAllServices();
                }

                TrimPrivate();

                FinishReadResource();

                return;
            }

            throw new CollisionException(this);
        }

        public void Trim()
        {
            if (WriteResource())
            {
                TrimPrivate();

                FinishWriteResource();

                return;
            }

            throw new CollisionException(this);
        }

        public void TabulaRasa()
        {
            if (WriteResource())
            {
                _currentId = 0;
                _graphElements = new BigList<AGraphElement>();
                IndexFactory.DeleteAllIndices();
                VertexCount = 0;
                EdgeCount = 0;

                FinishWriteResource();

                return;
            }

            throw new CollisionException(this);
        }

        public VertexModel CreateVertex(UInt32 creationDate, PropertyContainer[] properties = null)
        {
            if (WriteResource())
            {
                //create the new vertex
                var newVertex = new VertexModel(_currentId, creationDate, properties);

                //insert it
                _graphElements.SetValue(_currentId, newVertex);

                //increment the id
                Interlocked.Increment(ref _currentId);

                //Increase the vertex count
                VertexCount++;

                FinishWriteResource();

                return newVertex;
            }

            throw new CollisionException(this);
        }

        public EdgeModel CreateEdge(Int32 sourceVertexId, UInt16 edgePropertyId, Int32 targetVertexId,
                                    UInt32 creationDate, PropertyContainer[] properties = null)
        {
            if (WriteResource())
            {
                EdgeModel outgoingEdge = null;

                var sourceVertex = _graphElements.GetElement(sourceVertexId) as VertexModel;
                var targetVertex = _graphElements.GetElement(targetVertexId) as VertexModel;

                //get the related vertices
                if (sourceVertex != null && targetVertex != null)
                {
                    outgoingEdge = new EdgeModel(_currentId, creationDate, targetVertex, sourceVertex, properties);

                    //add the edge to the graph elements
                    _graphElements.SetValue(_currentId, outgoingEdge);

                    //increment the ids
                    Interlocked.Increment(ref _currentId);

                    //add the edge to the source vertex
                    sourceVertex.AddOutEdge(edgePropertyId, outgoingEdge);

                    //link the vertices
                    targetVertex.AddIncomingEdge(edgePropertyId, outgoingEdge);

                    //increase the edgeCount
                    EdgeCount++;
                }

                FinishWriteResource();

                return outgoingEdge;
            }

            throw new CollisionException(this);
        }

        public bool TryAddProperty(Int32 graphElementId, UInt16 propertyId, Object property)
        {
            if (WriteResource())
            {
                var success = false;
                AGraphElement graphElement = _graphElements.GetElement(graphElementId);
                if (graphElement != null)
                {
                    success = graphElement != null && graphElement.TryAddProperty(propertyId, property);
                }

                FinishWriteResource();

                return success;
            }

            throw new CollisionException(this);
        }

        public bool TryRemoveProperty(Int32 graphElementId, UInt16 propertyId)
        {
            if (WriteResource())
            {
                var graphElement = _graphElements.GetElement(graphElementId);

                var success = graphElement != null && graphElement.TryRemoveProperty(propertyId);

                FinishWriteResource();

                return success;
            }

            throw new CollisionException(this);
        }

        public bool TryRemoveGraphElement(Int32 graphElementId)
        {
            if (WriteResource())
            {
                AGraphElement graphElement = _graphElements.GetElement(graphElementId);

                if (graphElement == null)
                {
                    FinishWriteResource();

                    return false;
                }

                //used if an edge is removed
                List<UInt16> inEdgeRemovals = null;
                List<UInt16> outEdgeRemovals = null;

                try
                {
                    #region remove element

                    _graphElements.SetDefault(graphElementId);

                    if (graphElement is VertexModel)
                    {
                        #region remove vertex

                        var vertex = (VertexModel) graphElement;

                        #region out edges

                        var outgoingEdgeConatiner = vertex.GetOutgoingEdges();
                        if (outgoingEdgeConatiner != null)
                        {
                            for (var i = 0; i < outgoingEdgeConatiner.Count; i++)
                            {
                                var aOutEdgeContainer = outgoingEdgeConatiner[i];
                                for (var j = 0; j < aOutEdgeContainer.Edges.Count; j++)
                                {
                                    var aOutEdge = aOutEdgeContainer.Edges[j];

                                    //remove from incoming edges of target vertex
                                    aOutEdge.TargetVertex.RemoveIncomingEdge(aOutEdgeContainer.EdgePropertyId, aOutEdge);

                                    //remove the edge itself
                                    _graphElements.SetDefault(aOutEdge.Id);
                                }
                            }
                        }

                        #endregion

                        #region in edges

                        var incomingEdgeContainer = vertex.GetIncomingEdges();
                        if (incomingEdgeContainer != null)
                        {
                            for (var i = 0; i < incomingEdgeContainer.Count; i++)
                            {
                                var aInEdgeContainer = incomingEdgeContainer[i];
                                for (var j = 0; j < aInEdgeContainer.Edges.Count; j++)
                                {
                                    var aInEdge = aInEdgeContainer.Edges[j];

                                    //remove from outgoing edges of source vertex
                                    aInEdge.SourceVertex.RemoveOutGoingEdge(aInEdgeContainer.EdgePropertyId, aInEdge);

                                    //remove the edge itself
                                    _graphElements.SetDefault(aInEdge.Id);
                                }
                            }
                        }

                        #endregion

                        //update the EdgeCount --> hard way
                        RecalculateGraphElementCounter();

                        #endregion
                    }
                    else
                    {
                        #region remove edge

                        var edge = (EdgeModel) graphElement;

                        //remove from incoming edges of target vertex
                        inEdgeRemovals = edge.TargetVertex.RemoveIncomingEdge(edge);

                        //remove from outgoing edges of source vertex
                        outEdgeRemovals = edge.SourceVertex.RemoveOutGoingEdge(edge);

                        //update the EdgeCount --> easy way
                        EdgeCount--;

                        #endregion
                    }

                    #endregion
                }
                catch (Exception)
                {
                    #region restore

                    _graphElements.SetValue(graphElementId, graphElement);

                    if (graphElement is VertexModel)
                    {
                        #region restore vertex

                        var vertex = (VertexModel) graphElement;

                        #region out edges

                        var outgoingEdgeConatiner = vertex.GetOutgoingEdges();
                        if (outgoingEdgeConatiner != null)
                        {
                            for (var i = 0; i < outgoingEdgeConatiner.Count; i++)
                            {
                                var aOutEdgeContainer = outgoingEdgeConatiner[i];
                                for (var j = 0; j < aOutEdgeContainer.Edges.Count; j++)
                                {
                                    var aOutEdge = aOutEdgeContainer.Edges[j];

                                    //remove from incoming edges of target vertex
                                    aOutEdge.TargetVertex.AddIncomingEdge(aOutEdgeContainer.EdgePropertyId, aOutEdge);

                                    //reset the edge
                                    _graphElements.SetValue(aOutEdge.Id, aOutEdge);
                                }
                            }
                        }

                        #endregion

                        #region in edges

                        var incomingEdgeContainer = vertex.GetIncomingEdges();
                        if (incomingEdgeContainer != null)
                        {
                            for (var i = 0; i < incomingEdgeContainer.Count; i++)
                            {
                                var aInEdgeContainer = incomingEdgeContainer[i];
                                for (var j = 0; j < aInEdgeContainer.Edges.Count; j++)
                                {
                                    var aInEdge = aInEdgeContainer.Edges[j];

                                    //remove from outgoing edges of source vertex
                                    aInEdge.SourceVertex.AddOutEdge(aInEdgeContainer.EdgePropertyId, aInEdge);

                                    //reset the edge
                                    _graphElements.SetValue(aInEdge.Id, aInEdge);
                                }
                            }
                        }

                        #endregion

                        #endregion
                    }
                    else
                    {
                        #region restore edge

                        var edge = (EdgeModel) graphElement;

                        if (inEdgeRemovals != null)
                        {
                            for (var i = 0; i < inEdgeRemovals.Count; i++)
                            {
                                edge.TargetVertex.AddIncomingEdge(inEdgeRemovals[i], edge);
                            }
                        }

                        if (outEdgeRemovals != null)
                        {
                            for (var i = 0; i < outEdgeRemovals.Count; i++)
                            {
                                edge.SourceVertex.AddOutEdge(outEdgeRemovals[i], edge);
                            }
                        }

                        #endregion
                    }

                    //recalculate the counter
                    RecalculateGraphElementCounter();

                    #endregion

                    throw;
                }

                FinishWriteResource();

                return true;
            }

            throw new CollisionException(this);
        }

        #endregion

        #region IRead implementation

        public Boolean TryGetVertex(out VertexModel result, Int32 id)
        {
            if (ReadResource())
            {
                result = _graphElements.GetElement(id) as VertexModel;

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException(this);
        }

        public Boolean TryGetEdge(out EdgeModel result, Int32 id)
        {
            if (ReadResource())
            {
                result = _graphElements.GetElement(id) as EdgeModel;

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException(this);
        }

        public Boolean TryGetGraphElement(out AGraphElement result, Int32 id)
        {
            if (ReadResource())
            {
                result = _graphElements.GetElement(id);

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException(this);
        }

        public bool CalculateShortestPath(
            out List<Path> result,
            string algorithmname,
            Int32 sourceVertexId,
            Int32 destinationVertexId,
            Int32 maxDepth = 1,
            Double maxPathWeight = Double.MaxValue,
            Int32 maxResults = 1,
            PathDelegates.EdgePropertyFilter edgePropertyFilter = null,
            PathDelegates.VertexFilter vertexFilter = null,
            PathDelegates.EdgeFilter edgeFilter = null,
            PathDelegates.EdgeCost edgeCost = null,
            PathDelegates.VertexCost vertexCost = null)
        {
            IShortestPathAlgorithm algo;
            if (PluginFactory.TryFindPlugin(out algo, algorithmname))
            {
                algo.Initialize(this, null);

                if (ReadResource())
                {
                    result = algo.Calculate(sourceVertexId, destinationVertexId, maxDepth, maxPathWeight, maxResults,
                                            edgePropertyFilter,
                                            vertexFilter, edgeFilter, edgeCost, vertexCost);

                    FinishReadResource();

                    return result != null && result.Count > 0;
                }

                throw new CollisionException(this);
            }

            result = null;
            return false;
        }

        public bool GraphScan(out List<AGraphElement> result, UInt16 propertyId, IComparable literal,
                              BinaryOperator binOp)
        {
            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    result = FindElements(BinaryEqualsMethod, literal, propertyId);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(BinaryGreaterMethod, literal, propertyId);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(BinaryGreaterOrEqualMethod, literal, propertyId);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(BinaryLowerOrEqualMethod, literal, propertyId);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(BinaryLowerMethod, literal, propertyId);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(BinaryNotEqualsMethod, literal, propertyId);
                    break;

                default:
                    result = new List<AGraphElement>();

                    break;
            }

            #endregion

            return result.Count > 0;
        }

        public bool IndexScan(out ReadOnlyCollection<AGraphElement> result, String indexId, IComparable literal,
                              BinaryOperator binOp)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:

                    index.TryGetValue(out result, literal);
                    break;

                case BinaryOperator.Greater:
                    result = FindElementsIndex(BinaryGreaterMethod, literal, index);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElementsIndex(BinaryGreaterOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElementsIndex(BinaryLowerOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.Lower:
                    result = FindElementsIndex(BinaryLowerMethod, literal, index);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElementsIndex(BinaryNotEqualsMethod, literal, index);
                    break;

                default:
                    result = null;
                    return false;
            }

            #endregion

            return result.Count > 0;
        }

        public bool RangeIndexScan(out ReadOnlyCollection<AGraphElement> result, String indexId, IComparable leftLimit,
                                   IComparable rightLimit, bool includeLeft, bool includeRight)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var rangeIndex = index as IRangeIndex;
            if (rangeIndex != null)
            {
                return rangeIndex.Between(out result, leftLimit, rightLimit, includeLeft, includeRight);
            }

            result = null;
            return false;
        }

        public bool FulltextIndexScan(out FulltextSearchResult result, String indexId, string searchQuery)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var fulltextIndex = index as IFulltextIndex;
            if (fulltextIndex != null)
            {
                return fulltextIndex.TryQuery(out result, searchQuery);
            }

            result = null;
            return false;
        }

        public bool SpatialIndexScan(out ReadOnlyCollection<AGraphElement> result, String indexId, IGeometry geometry)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var spatialIndex = index as ISpatialIndex;
            if (spatialIndex != null)
            {
                return spatialIndex.TryGetValue(out result, geometry);
            }

            result = null;
            return false;
        }

        public void Save(String path, UInt32 savePartitions = 5)
        {
            if (ReadResource())
            {
                PersistencyFactory.Save(this, _graphElements, path, savePartitions, _currentId);

                FinishReadResource();

                return;
            }

            throw new CollisionException(this);
        }

        #endregion

        #region public methods

        /// <summary>
        ///   Shutdown this Fallen-8 server.
        /// </summary>
        public void Shutdown()
        {
            ServiceFactory.ShutdownAllServices();
        }

        #endregion

        #region private helper methods

        /// <summary>
        ///   Finds the elements.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='propertyId'> Property identifier. </param>
        private List<AGraphElement> FindElements(BinaryOperatorDelegate finder, IComparable literal, UInt16 propertyId)
        {
            if (ReadResource())
            {
                var result = _graphElements.FindElements(
                    aGraphElement =>
                        {
                            Object property;
                            return aGraphElement.TryGetProperty(out property, propertyId) &&
                                   finder(property as IComparable, literal);
                        });
                FinishReadResource();

                return result;
            }

            throw new CollisionException(this);
        }

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        private static ReadOnlyCollection<AGraphElement> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                           IComparable literal, IIndex index)
        {
            return new ReadOnlyCollection<AGraphElement>(index.GetKeyValues()
                                                             .AsParallel()
                                                             .Select(aIndexElement => new KeyValuePair<IComparable, ReadOnlyCollection<AGraphElement>>((IComparable)aIndexElement.Key, aIndexElement.Value))
                                                             .Where(aTypedIndexElement => finder(aTypedIndexElement.Key, literal))
                                                             .Select(_ => _.Value)
                                                             .SelectMany(__ => __)
                                                             .Distinct()
                                                             .ToList());
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for equality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }

        /// <summary>
        ///   Trims the Fallen-8.
        /// </summary>
        private void TrimPrivate()
        {
            for (var i = 0; i <= _currentId; i++)
            {
                AGraphElement graphElement = _graphElements.GetElement(i);
                if (graphElement != null)
                {
                    graphElement.Trim();
                }
            }

#if __MonoCS__
    //mono specific code
#else
            GC.Collect();
            GC.Collect();
            GC.WaitForFullGCComplete();
            GC.WaitForPendingFinalizers();
#endif

#if __MonoCS__
    //mono specific code
#else
            var errorCode = SaveNativeMethods.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
#endif

            RecalculateGraphElementCounter();

        }

        /// <summary>
        /// Recalculates the count of the graph elements
        /// </summary>
        private void RecalculateGraphElementCounter()
        {
            EdgeCount = _graphElements.GetCountOf<EdgeModel>();
            VertexCount = _graphElements.GetCountOf<VertexModel>();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            TabulaRasa();

            _graphElements = null;

            IndexFactory.DeleteAllIndices();
            IndexFactory = null;

            ServiceFactory.ShutdownAllServices();
            ServiceFactory = null;
        }

        #endregion
    }
}