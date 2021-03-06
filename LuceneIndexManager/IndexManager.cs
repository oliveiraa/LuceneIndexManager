﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Util;
using LuceneIndexManager.Facets;
using LuceneIndexManager.Util;
using ThrowHelper;

namespace LuceneIndexManager
{
    public class IndexManager
    {
        private Dictionary<int, List<Facet>> _facets = new Dictionary<int, List<Facet>>();
        //TODO: Make it configurable
        private readonly string _facetsLocation = @"C:\temp\facets\";

        public IndexManager()
        {
            this.RegisteredIndexSources = new Dictionary<int, IIndexDefinition>();
        }

        public Dictionary<int, IIndexDefinition> RegisteredIndexSources { get; private set; }

        public void RegisterIndex<T>() where T : IIndexDefinition
        {
            var source = Activator.CreateInstance<T>();
            this.RegisterIndex(source);
        }

        public void RegisterIndex(IIndexDefinition source)
        {
            if (this.RegisteredIndexSources.ContainsKey(source.GetType().GetHashCode()))
                throw new ArgumentException("Only one index of the same type can be registered. There is no reason why you would want to register the same index twice. :)");

            this.RegisteredIndexSources.Add(source.GetType().GetHashCode(), source);
        }

        /// <summary>
        /// Creates all registered indexes
        /// </summary>
        /// <returns>Number of indexes created</returns>
        public int CreateIndexes()
        {
            "{0} index(es) registered".Debug(this.RegisteredIndexSources.Count);

            "Creating index(es)".Info();

            var indexesCreated = 0;

            foreach (var index in this.RegisteredIndexSources)
            {
                CreateIndex(index.Value);
                indexesCreated++;
            }

            "{0} index(e)s created".Info(indexesCreated);

            return indexesCreated;
        }

        private void CreateIndex(IIndexDefinition index)
        {
            using (var indexWriter = index.GetIndexWriter())
            {
                //If we are creating a new index explicitly make sure we start with an empty index
                indexWriter.DeleteAll();

                foreach (var document in index.GetAllDocuments().AsParallel())
                {
                    indexWriter.AddDocument(document);
                }

                indexWriter.Commit();
                indexWriter.Optimize();
                indexWriter.Close();

                CreateFacets(index);
            }
        }

        private void CreateFacets(IIndexDefinition index)
        {
            var facetsToCreate = index.GetFacetsDefinition();

            var builder = new FacetBuilder(index.GetIndexSearcher().GetIndexReader());

            //TODO: Add checking to make sure duplicated facets are not created and stored
            var facets = builder.CreateFacets(facetsToCreate);

            this.StoreFacets(facets, this._facetsLocation);

            _facets.Add(index.GetHashCode(), facets);
        }

        public void StoreFacets(List<Facet> facets, string path)
        {
            Throw.IfArgumentNull(facets);
            Throw.IfArgumentNullOrEmpty(path);

            "Storing facet(s)".Info();

            foreach (var facet in facets)
            {
                StoreFacet(facet, path);
            }

            "Facet(s) stored".Info();
        }

        private void StoreFacet(Facet facet, string path)
        {
            var pathToStoreFacet = Path.Combine(path, facet.UniqueName + ".facet");

            "Storing facet {0} in {1}".Debug(facet, path);

            if (!Directory.Exists(path))
            {
                "The path {0} to store facets does not exists. Creating it...".Info(path);
                Directory.CreateDirectory(path);
                "Path successfully created".Info();
            }

            if (File.Exists(pathToStoreFacet))
            {
                "Facet already exists in disk. {0}. Path: {1}".Error(facet.ToString(), pathToStoreFacet);
                throw new InvalidOperationException("Facet already exists in disk");
            }
            else
            {
                using (var fs = File.CreateText(pathToStoreFacet))
                {
                    fs.Write(facet.UniqueName);
                    fs.Write("|");
                    fs.Write(facet.Field);
                    fs.Write("|");
                    fs.Write(facet.DisplayName);
                    fs.Write(Environment.NewLine);

                    foreach (var value in facet.Values)
                    {
                        fs.Write(value.Item1);
                        fs.Write("|");
                        fs.Write(value.Item2.SerializeToBase64String());
                        fs.Write(Environment.NewLine);
                    }
                }
            }
        }

        #region Searching Operations

        public QueryParser GetQueryParser<T>() where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            return index.GetDefaultQueryParser();
        }

        public FacetSearchResult SearchWithFacets<T>(Query query, int topResults, List<FacetMatch> filterByFacet) where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            var facetsToMatch = this._facets[index.GetHashCode()];            
            var searcher = this.GetSearcher<T>();
            var indexReader = searcher.GetIndexReader();
            var searchQueryFilter = new QueryWrapperFilter(query);            
            var facetAsFiter = facetsToMatch.First(x => x.Id == filterByFacet.First().Id).Values.First(x => x.Item1 == filterByFacet.First().Value);
            var facetMatcher = new FacetMatcher(searchQueryFilter, facetAsFiter.Item3, indexReader);
            
            var hits = searcher.Search(query, facetAsFiter.Item3, topResults);            

            var facets = facetMatcher.GetAllMatches(facetsToMatch);

            return new FacetSearchResult()
            {
                Facets = facets,
                Hits = hits
            };
        }

        public FacetSearchResult SearchWithFacets<T>(Query query, int topResults) where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            var searcher = this.GetSearcher<T>();
            var indexReader = searcher.GetIndexReader();
            var searchQueryFilter = new QueryWrapperFilter(query);            

            var facetMatcher = new FacetMatcher(searchQueryFilter, indexReader);

            var hits = searcher.Search(query, topResults);
            var facetsToMatch = this._facets[index.GetHashCode()];
            var facets = facetMatcher.GetAllMatches(facetsToMatch);

            return new FacetSearchResult()
            {
                Facets = facets,
                Hits = hits
            };
        }

        private IndexSearcher GetSearcher<T>() where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            var searcher = index.GetIndexSearcher();
            return searcher;
        }

        //TODO: This is internal just to be available for testing. Need to turn it to private and find another way to test it
        internal IIndexDefinition FindRegisteredIndex(Type t)
        {
            var hash = t.GetHashCode();
            var index = this.RegisteredIndexSources[hash];

            return index;
        }

        #endregion
    }
}
