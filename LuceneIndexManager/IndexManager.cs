﻿using System;
using System.Collections.Generic;
using System.Linq;
using Lucene.Net.Search;
using Lucene.Net.Util;
using LuceneIndexManager.Facets;

namespace LuceneIndexManager
{
    public class IndexManager
    {
        private Dictionary<int, List<Facet>> _facets = new Dictionary<int,List<Facet>>();
        
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
            
            this.RegisteredIndexSources.Add(source.GetType().GetHashCode(),  source);
        }

        public int CreateIndexes()
        {
            var indexesCreated = 0;
            
            foreach (var index in this.RegisteredIndexSources)
            {
                CreateIndex(index.Value);
                indexesCreated++;
            }

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

            var builder = new FacetBuilder(index);
            var facets = builder.CreateFacets(facetsToCreate);

            _facets.Add(index.GetHashCode(), facets);
        }        

        #region Searching Operations

        public IndexSearcher GetSearcher<T>() where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            var searcher = index.GetIndexSearcher();
            return searcher;
        }

        public FacetSearchResult SearchWithFacets<T>(string query) where T : IIndexDefinition
        {
            var index = this.FindRegisteredIndex(typeof(T));
            var searcher = this.GetSearcher<T>();
            var indexReader = searcher.GetIndexReader();
            var queryParser = index.GetDefaultQueryParser();
            var parsedQuery = queryParser.Parse(query);

            var searchQueryFilter = new QueryWrapperFilter(parsedQuery);

            var hits = searcher.Search(parsedQuery);

            var facets = this._facets.First().Value.Select(x =>
            {
                var bits = new OpenBitSetDISI(searchQueryFilter.GetDocIdSet(indexReader).Iterator(), indexReader.MaxDoc());
                bits.And(x.MatchingDocuments);
                var count = bits.Cardinality();

                return new FacetMatch() { Count = count, Value = x.Value };
            }).ToList();

            return new FacetSearchResult()
            {
                Facets = facets,
                Hits = hits
            };
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
