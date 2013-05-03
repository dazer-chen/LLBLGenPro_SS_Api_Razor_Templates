﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SD.LLBLGen.Pro.ORMSupportClasses;
using ServiceStack.CacheAccess;
using ServiceStack.ServiceHost;
using ServiceStack.Text;
using Northwind.Data.Dtos;
using Northwind.Data.EntityClasses;
using Northwind.Data.FactoryClasses;
using Northwind.Data.HelperClasses;
using Northwind.Data.Helpers;
using Northwind.Data.ServiceInterfaces;
using Northwind.Data.Services;

namespace Northwind.Data.ServiceRepositories
{
    public abstract class EntityServiceRepositoryBase<TDto, TEntity, TEntityFactory, TEntityFieldIndexEnum> : IEntityServiceRepository<TDto>
        where TDto : CommonDtoBase
        where TEntity: CommonEntityBase, new() 
        where TEntityFactory : EntityFactoryBase2<TEntity>, new() 
        where TEntityFieldIndexEnum : struct, IConvertible
    {
        public ICacheClient CacheClient { get; set; }
        public abstract IDataAccessAdapterFactory DataAccessAdapterFactory { get; set; }
        
        protected abstract EntityType EntityType { get; }
       
        internal virtual IDictionary< string, IEntityField2[] > UniqueConstraintMap
        {
            get { return new Dictionary< string, IEntityField2[] >(); }
            set { }
        }

        internal virtual IDictionary<string, IEntityField2> FieldMap
        {
            get { return GetEntityTypeFieldMap(EntityType); }
            set { }
        }

        internal virtual IDictionary<string, IPrefetchPathElement2> IncludeMap
        {
            get { return GetEntityTypeIncludeMap(EntityType); }
            set { }
        }

        internal virtual IDictionary<string, IEntityRelation> RelationMap
        {
            get { return GetEntityTypeRelationMap(EntityType); }
            set { }
        }        
        
        #region Fetch Methods
        
        public EntityMetaDetailsResponse GetEntityMetaDetails(ServiceStack.ServiceInterface.Service service)
        {
            // The entity meta details don't change per entity type, so cache these for performance
            var cacheKey = string.Format("{0}-meta-details", EntityType.ToString().ToLower());
            var metaDetails = CacheClient.Get<EntityMetaDetailsResponse>(cacheKey);
            if (metaDetails != null)
                return metaDetails;
                
            var request = service.RequestContext.Get<IHttpRequest>();
            var appUri = request.GetApplicationUrl().TrimEnd('/');
            var baseServiceUri = appUri + request.PathInfo.Replace("/meta", "");
            var queryString = request.QueryString["format"] != null ? "&format=" + request.QueryString["format"] : "";
            var pkCount = FieldMap.Count(pk => pk.Value.IsPrimaryKey);
            var fields = new List<Link>();
            foreach (var f in FieldMap)
            {
                var isUnique = false;
                var ucs = UniqueConstraintMap.Where(uc => uc.Value.Any(x => x.Name.Equals(f.Key, StringComparison.InvariantCultureIgnoreCase))).ToArray();
                var link = Link.Create(
                  f.Key.ToCamelCase(), f.Value.DataType.Name, "field",
                  string.Format("{0}?select={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                  string.Format("The {0} field for the {1} resource.", f.Value.Name, typeof (TDto).Name),
                  new Dictionary<string, string>()
                );
                var props = new SortedDictionary<string, string>();
                props.Add("index", f.Value.FieldIndex.ToString(CultureInfo.InvariantCulture));
                if (f.Value.IsPrimaryKey)
                {
                    props.Add("isPrimaryKey", f.Value.IsPrimaryKey.ToString().ToLower());
                    if (pkCount == 1) isUnique = true;
                }
                if (f.Value.IsForeignKey)
                    props.Add("isForeignKey", "true");

                var ucNames = new List<string>();
                foreach (var uc in ucs)
                {
                    if (uc.Value.Count() == 1) isUnique = true;
                    ucNames.Add(uc.Key.ToLower());
                }
                if (ucNames.Any())
                    props.Add("partOfUniqueConstraints", string.Join(",", ucNames.ToArray()));
                if (isUnique)
                    props.Add("isUnique", "true");
                if (f.Value.IsOfEnumDataType)
                    props.Add("isOfEnumDataType", "true");
                if (f.Value.IsReadOnly)
                    props.Add("isReadOnly", "true");
                if (f.Value.IsNullable)
                    props.Add("isNullable", "true");
                if (f.Value.IsOfEnumDataType)
                    props.Add("isEnumType", "true");
                if (f.Value.MaxLength > 0)
                    props.Add("maxLength", f.Value.MaxLength.ToString(CultureInfo.InvariantCulture));
                if (f.Value.Precision > 0)
                    props.Add("precision", f.Value.Precision.ToString(CultureInfo.InvariantCulture));
                if (f.Value.Scale > 0)
                    props.Add("scale", f.Value.Scale.ToString(CultureInfo.InvariantCulture));
                link.Properties = new Dictionary<string, string>(props);
                fields.Add(link);
            }

            var includes = new List<Link>();
            foreach (var f in IncludeMap)
            {
                var relationType = "";
                switch (f.Value.TypeOfRelation)
                {
                    case RelationType.ManyToMany:
                        relationType = "n:n";
                        break;
                    case RelationType.ManyToOne:
                        relationType = "n:1";
                        break;
                    case RelationType.OneToMany:
                        relationType = "1:n";
                        break;
                    case RelationType.OneToOne:
                        relationType = "1:1";
                        break;
                }
                var relatedDtoContainerName =
                    (Enum.GetName(typeof (EntityType), f.Value.ToFetchEntityType) ?? "").Replace("Entity", "");
                var link = Link.Create(
                    f.Key.ToCamelCase(),
                    (relationType.EndsWith("n") ? relatedDtoContainerName + "Collection" : relatedDtoContainerName),
                    "include",
                    string.Format("{0}?include={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                    string.Format(
                        "The {0} field for the {1} resource to include in the results returned by a query.",
                        f.Value.PropertyName,
                        typeof (TDto).Name),
                    new Dictionary<string, string>
                        {
                            {"field", f.Value.PropertyName.ToCamelCase()},
                            {
                                "relatedType",
                                (Enum.GetName(typeof (EntityType), f.Value.ToFetchEntityType) ?? "").Replace("Entity", "")
                            },
                            {"relationType", relationType}
                        });
                includes.Add(link);
            }

            var relations = new List<Link>();
            foreach (var f in RelationMap)
            {
                var isPkSide = f.Value.StartEntityIsPkSide;
                var isFkSide = !f.Value.StartEntityIsPkSide;
                var pkFieldCore = f.Value.GetAllPKEntityFieldCoreObjects().FirstOrDefault();
                var fkFieldCore = f.Value.GetAllFKEntityFieldCoreObjects().FirstOrDefault();
                var thisField = isPkSide
                                    ? (pkFieldCore == null ? "" : pkFieldCore.Name)
                                    : (fkFieldCore == null ? "" : fkFieldCore.Name);
                var relatedField = isFkSide
                                    ? (pkFieldCore == null ? "" : pkFieldCore.Name)
                                    : (fkFieldCore == null ? "" : fkFieldCore.Name);
                var thisEntityAlias = isPkSide
                                    ? (pkFieldCore == null ? "": pkFieldCore.ActualContainingObjectName.Replace("Entity", ""))
                                    : (fkFieldCore == null ? "": fkFieldCore.ActualContainingObjectName.Replace("Entity", ""));
                var relatedEntityAlias = isFkSide
                                    ? (pkFieldCore == null ? "": pkFieldCore.ActualContainingObjectName.Replace("Entity", ""))
                                    : (fkFieldCore == null ? "": fkFieldCore.ActualContainingObjectName.Replace("Entity", ""));
                var relationType = "";
                switch (f.Value.TypeOfRelation)
                {
                    case RelationType.ManyToMany:
                        relationType = "n:n";
                        break;
                    case RelationType.ManyToOne:
                        relationType = "n:1";
                        break;
                    case RelationType.OneToMany:
                        relationType = "1:n";
                        break;
                    case RelationType.OneToOne:
                        relationType = "1:1";
                        break;
                }
                var link = Link.Create(
                  f.Key.ToCamelCase(),
                  relationType.EndsWith("n") ? relatedEntityAlias + "Collection" : relatedEntityAlias, "relation",
                  string.Format("{0}?relations={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                  string.Format(
                    "The relation '{0}' for the {1} resource between a {2} (PK) and a {3} (FK) resource.",
                    f.Value.MappedFieldName,
                    typeof (TDto).Name, f.Value.AliasStartEntity.Replace("Entity", ""),
                    f.Value.AliasEndEntity.Replace("Entity", "")),
                  new Dictionary<string, string>
                  {
                    {"field", f.Value.MappedFieldName.ToCamelCase()},
                    {"joinHint", f.Value.JoinType.ToString().ToLower()},
                    {"relationType", relationType},
                    {"isPkSide", isPkSide.ToString().ToLower()},
                    {"isFkSide", isFkSide.ToString().ToLower()},
                    {"isWeakRelation", f.Value.IsWeak.ToString().ToLower()},
                    {"pkTypeName", isPkSide ? thisEntityAlias : relatedEntityAlias},
                    {
                      "pkTypeField",
                      isPkSide ? thisField.ToCamelCase() : relatedField.ToCamelCase()
                    },
                    {"fkTypeName", isFkSide ? thisEntityAlias : relatedEntityAlias},
                    {
                      "fkTypeField",
                      isFkSide ? thisField.ToCamelCase() : relatedField.ToCamelCase()
                    },
                  });
                relations.Add(link);
                // add relation to fields list as well
                fields.Add(Link.Create(
                  f.Value.MappedFieldName.ToCamelCase(),
                  relationType.EndsWith("n") ? relatedEntityAlias + "Collection": relatedEntityAlias,
                  "field", null,
                  string.Format("The {0} field for the {1} resource.", f.Value.MappedFieldName,
                        typeof (TDto).Name), new Dictionary<string, string>
                          {
                            {"relation", f.Value.MappedFieldName.ToCamelCase()},
                            {"relationType", relationType},
                            {"isPkSide", isPkSide.ToString().ToLower()},
                            {"isFkSide", isFkSide.ToString().ToLower()},
                          }));
            }

            metaDetails = new EntityMetaDetailsResponse()
                {
                    Fields = fields.ToArray(),
                    Includes = includes.ToArray(),
                    Relations = relations.ToArray()
                };
            CacheClient.Set(cacheKey, metaDetails);
            return metaDetails;
        }

        internal EntityCollection<TEntity> Fetch(IDataAccessAdapter adapter, SortExpression sortExpression, ExcludeIncludeFieldsList excludedIncludedFields,
            IPrefetchPath2 prefetchPath, IRelationPredicateBucket predicateBucket, int pageNumber,
                                                 int pageSize, int limit, out int totalItemCount)
        {
            var entities = new EntityCollection<TEntity>(new TEntityFactory());
            totalItemCount = adapter.GetDbCount(entities, predicateBucket);
            if (limit > 0)
            {
                adapter.FetchEntityCollection(entities, predicateBucket, limit,
                                              sortExpression, prefetchPath,
                                              excludedIncludedFields);
            }
            else
            {
                adapter.FetchEntityCollection(entities, predicateBucket, 0,
                                              sortExpression, prefetchPath,
                                              excludedIncludedFields, pageNumber, pageSize);
            }
            return entities;
        }      

        internal void FixupLimitAndPagingOnRequest(GetCollectionRequest request)
        {
            if (request.PageNumber > 0 || request.PageSize > 0)
                request.Limit = 0; // override the limit, paging takes precedence if specified

            if (request.PageNumber < 1) request.PageNumber = 1;
            if (request.PageSize < 1) request.PageSize = 10;
        }
        
        #endregion

        #region Conversion Methods

        #region Field Getters
        
        internal virtual IEntityField2 GetField(string fieldName, bool throwIfDoesNotExist)
        {
            if (!typeof(TEntityFieldIndexEnum).IsEnum)
                throw new ArgumentException("T must be an enumerated type");

            TEntityFieldIndexEnum t;
            if (!Enum.TryParse(fieldName, true, out t))
            {
                if (throwIfDoesNotExist)
                    throw new ArgumentException(string.Format("Requested value '{0}' was not found in {1}.", fieldName,
                                                              typeof(TEntityFieldIndexEnum)));
                return null;
            }

            return EntityFieldFactory.Create((Enum) (object) t);
        }

        internal virtual IEntityField2 GetRelatedField(string fieldInfo, List<IEntityRelation> relations)
        {
            var fieldSegments = fieldInfo.Trim('.').Split('.');
            var fieldParts = fieldSegments.Take(fieldSegments.Length - 1).ToArray();
            var fieldName = fieldSegments[fieldSegments.Length - 1];

            var entityType = EntityType;
            for (var i = 0; i < fieldParts.Length; i++)
            {
                var isLastEntity = i == fieldParts.Length - 1;
                var fieldPart = fieldParts[i];

                var relationMap = GetEntityTypeRelationMap(entityType);
                var relation =
                    relationMap.FirstOrDefault(
                        r => r.Value.MappedFieldName.Equals(fieldPart, StringComparison.OrdinalIgnoreCase));
                if (relation.Equals(default(KeyValuePair<string, IEntityRelation>)))
                    return null;
                relations.AddIfNotExists(relation.Value);

                var prefetchMap = GetEntityTypeIncludeMap(entityType);
                var prefetch =
                    prefetchMap.FirstOrDefault(p => p.Key.Equals(fieldPart, StringComparison.OrdinalIgnoreCase));
                if (prefetch.Equals(default(KeyValuePair<string, IPrefetchPathElement2>)))
                    return null;

                entityType = (EntityType) prefetch.Value.ToFetchEntityType;
                if (isLastEntity)
                {
                    var fieldKvpToReturn =
                        GetEntityTypeFieldMap(entityType)
                            .FirstOrDefault(f => f.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                    var fieldToReturn = fieldKvpToReturn.Equals(default(KeyValuePair<string, IEntityField2>))
                                            ? null
                                            : fieldKvpToReturn.Value;
                    return fieldToReturn;
                }
            }
            return null;
        }
        
        #endregion

        #region String to Sort Conversion methods
        
        internal virtual SortExpression ConvertStringToSortExpression(string sortStr)
        {
            var sortExpression = new SortExpression();
            if (!string.IsNullOrEmpty(sortStr))
            {
                var sortClauses = sortStr.Split(new[] {';', ','}, StringSplitOptions.None);
                foreach (var sortClause in sortClauses)
                {
                    var sortClauseElements = sortClause.Split(new[] {':'}, StringSplitOptions.RemoveEmptyEntries);
                    var sortField = GetField(sortClauseElements[0], false);
                    if (sortField == null)
                        continue;

                    sortExpression.Add(new SortClause(
                                           sortField, null,
                                           sortClauseElements.Length > 1 &&
                                           sortClauseElements[1].Equals("desc",
                                                                        StringComparison.OrdinalIgnoreCase)
                                               ? SortOperator.Descending
                                               : SortOperator.Ascending
                                           ));
                }
            }
            return sortExpression;
        }
        
        #endregion        
        
        #region String to ExcludeIncludedFields Conversion methods
        
        internal ExcludeIncludeFieldsList ConvertStringToExcludedIncludedFields(string selectStr)
        {
            if (string.IsNullOrWhiteSpace(selectStr))
                return null;

            var fieldNames = selectStr
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.IndexOf('.') == -1)
                .ToArray();

            return ConvertEntityTypeFieldNamesToExcludedIncludedFields(EntityType, fieldNames);
        }
        
        #endregion

        #region String to PrefetchPath Conversion methods

        internal virtual IPrefetchPath2 ConvertStringToPrefetchPath(string prefetchStr, string selectStr)
        {
            if (string.IsNullOrWhiteSpace(prefetchStr))
                return null;

            // Break up the selectStr into a dictionary of keys and values
            // where the key is the path (i.e.: products.supplier)
            // and the value is the field name (i.e.: companyname)
            var prefetchFieldKeysAndNames = (selectStr ?? "")
                .Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.IndexOf('.') > 0)
                .Select(s => s.Trim('.').ToLowerInvariant());
            var prefetchFieldNamesDictionary = new Dictionary<string, List<string>>();
            foreach (var s in prefetchFieldKeysAndNames)
            {
                var key = s.Substring(0, s.LastIndexOf('.'));
                var value = s.Substring(s.LastIndexOf('.') + 1);
                if (prefetchFieldNamesDictionary.ContainsKey(key))
                    prefetchFieldNamesDictionary[key].AddIfNotExists(value);
                else
                    prefetchFieldNamesDictionary.Add(key, new List<string>(new[] {value}));
            }

            var prefetchStrs = prefetchStr.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);
            var nodeLeaves =
                prefetchStrs.Select(n => n.Split(new[] {'.'}, StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var rootNode = new PrefetchElementStringRepresentation {Name = "root", MaxNumberOfItemsToReturn = 0};
            foreach (var nodeLeaf in nodeLeaves)
                BuildTreeNode(rootNode, nodeLeaf, 0);

            var prefetch = new PrefetchPath2(EntityType);
            foreach (var prefetchRepresentation in rootNode.Children)
            {
                ExcludeIncludeFieldsList prefetchPathElementIncludeFieldList;
                IPrefetchPathElement2 prefetchPathElement =
                    ConvertPrefetchRepresentationToPrefetchPathElement(EntityType, prefetchRepresentation,
                                                                       prefetchFieldNamesDictionary,
                                                                       prefetchRepresentation.Name.ToLowerInvariant(),
                                                                       out prefetchPathElementIncludeFieldList);
                if (prefetchPathElement != null)
                    prefetch.Add(prefetchPathElement, prefetchPathElementIncludeFieldList);
            }
            return prefetch.Count > 0 ? prefetch : null;
        }

        private IPrefetchPathElement2 ConvertPrefetchRepresentationToPrefetchPathElement(EntityType parentEntityType,
                                                                                         PrefetchElementStringRepresentation
                                                                                             prefetchRepresentation,
                                                                                         Dictionary
                                                                                             <string, List<string>>
                                                                                             prefetchFieldNamesDictionary,
                                                                                         string fieldNamesKey,
                                                                                         out ExcludeIncludeFieldsList
                                                                                             includeFieldList)
        {
            includeFieldList = null;

            if (prefetchRepresentation == null)
                return null;

            var includeMap = GetEntityTypeIncludeMap(parentEntityType);

            IPrefetchPathElement2 newElement = null;
            var rr =
                includeMap.FirstOrDefault(
                    rm => rm.Key.Equals(prefetchRepresentation.Name, StringComparison.InvariantCultureIgnoreCase));
            if (!rr.Equals(default(KeyValuePair<string, IPrefetchPathElement2>)))
            {
                newElement = rr.Value;

                foreach (var childRepresentation in prefetchRepresentation.Children)
                {
                    ExcludeIncludeFieldsList subElementIncludeFieldList;
                    var subElement =
                        ConvertPrefetchRepresentationToPrefetchPathElement((EntityType) newElement.ToFetchEntityType,
                                                                           childRepresentation,
                                                                           prefetchFieldNamesDictionary,
                                                                           string.Concat(fieldNamesKey, ".",
                                                                                         childRepresentation.Name
                                                                                                            .ToLowerInvariant
                                                                                             ()),
                                                                           out subElementIncludeFieldList);
                    if (subElement != null)
                        newElement.SubPath.Add(subElement, subElementIncludeFieldList);
                }
            }

            if (newElement != null)
            {
                // Determin if there is a max amount of records to return for this prefetch element
                if (newElement.MaxAmountOfItemsToReturn < prefetchRepresentation.MaxNumberOfItemsToReturn)
                    newElement.MaxAmountOfItemsToReturn = prefetchRepresentation.MaxNumberOfItemsToReturn;

                // Determine if there are field name restrictions applied to the prefetched item
                if (prefetchFieldNamesDictionary.ContainsKey(fieldNamesKey))
                {
                    includeFieldList = ConvertEntityTypeFieldNamesToExcludedIncludedFields(
                        (EntityType) newElement.ToFetchEntityType, prefetchFieldNamesDictionary[fieldNamesKey]);
                }
            }

            return newElement;
        }

        // Define other methods and classes here
        private void BuildTreeNode(PrefetchElementStringRepresentation iteratorNode, string[] nodeLeaf, int index)
        {
            if (index >= nodeLeaf.Length)
                return; // we're done

            var nodeStr = nodeLeaf[index];
            var nodeStrArr = nodeStr.Split(':');
            var name = nodeStrArr[0];
            var max = 0;
            if (nodeStrArr.Length == 2) int.TryParse(nodeStrArr[1], out max);

            var n = iteratorNode.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n == null)
            {
                n = new PrefetchElementStringRepresentation {Name = name, MaxNumberOfItemsToReturn = max};
                iteratorNode.Children.Add(n);
            }
            else if (n.MaxNumberOfItemsToReturn < max)
            {
                n.MaxNumberOfItemsToReturn = max;
            }
            BuildTreeNode(n, nodeLeaf, ++index);
        }

        private class PrefetchElementStringRepresentation
        {
            public PrefetchElementStringRepresentation()
            {
                Children = new List<PrefetchElementStringRepresentation>();
            }

            public string Name { get; set; }
            public int MaxNumberOfItemsToReturn { get; set; }
            public IList<PrefetchElementStringRepresentation> Children { get; private set; }
        }

        #endregion

        #region String to Relation Conversion methods
        
        internal virtual IRelationPredicateBucket ConvertStringToRelationPredicateBucket(string filterStr,
                                                                                         string relationStr)
        {
            var predicateBucket = new RelationPredicateBucket();

            //var relations = ConvertStringToRelations(relationStr);
            //if (relations != null && relations.Count > 0)
            //    predicateBucket.Relations.AddRange(relations.ToArray());

            var inferredRelationsList = new List<IEntityRelation>();
            var predicate = ConvertStringToPredicate(filterStr, inferredRelationsList);
            if (inferredRelationsList.Count > 0)
                predicateBucket.Relations.AddRange(inferredRelationsList);
            if (predicate != null)
                predicateBucket.PredicateExpression.Add(predicate);
            return predicateBucket;
        }

        //private ICollection<IRelation> ConvertStringToRelations(string relationStr)
        //{
        //    if (string.IsNullOrWhiteSpace(relationStr))
        //        return null;

        //    var relationStrs = relationStr.Split(new[]{':', ','}, StringSplitOptions.RemoveEmptyEntries);
        //    var relations = new List<IRelation>();
        //    foreach (var r in relationStrs)
        //    {
        //        var rr = RelationMap.FirstOrDefault(rm => rm.Key.Equals(r, StringComparison.InvariantCultureIgnoreCase));
        //        if (!rr.Equals(default(KeyValuePair<string, IEntityRelation>)))
        //            relations.Add(rr.Value);
        //    }
        //    return relations.Count > 0 ? relations.ToArray() : null;
        //}

        #endregion

        #region String to Predicate Conversion methods
        
        private IPredicate ConvertStringToPredicate(string filterStr, List<IEntityRelation> inferredRelationsList)
        {
            var filterNode = FilterParser.Parse(filterStr);
            var predicate = BuildPredicateTree(filterNode, inferredRelationsList);
            return predicate;
        }

        private IPredicate BuildPredicateTree(FilterNode filterNode, List<IEntityRelation> inferredRelationsList)
        {
            if (filterNode.NodeCount > 0)
            {
                if (filterNode.NodeType == FilterNodeType.Root && filterNode.Nodes.Count > 0)
                {
                    if (filterNode.NodeCount == 1)
                        return BuildPredicateTree(filterNode.Nodes[0], inferredRelationsList);

                    var predicate = new PredicateExpression();
                    foreach (var childNode in filterNode.Nodes)
                    {
                        var newPredicate = BuildPredicateTree(childNode, inferredRelationsList);
                        if (newPredicate != null)
                            predicate.AddWithAnd(newPredicate);
                    }
                    return predicate;
                }
                else if (filterNode.NodeType == FilterNodeType.AndExpression ||
                    filterNode.NodeType == FilterNodeType.OrExpression)
                {
                    var predicate = new PredicateExpression();
                    foreach (var childNode in filterNode.Nodes)
                    {
                        var newPredicate = BuildPredicateTree(childNode, inferredRelationsList);
                        if (newPredicate != null)
                        {
                            if (filterNode.NodeType == FilterNodeType.OrExpression)
                                predicate.AddWithOr(newPredicate);
                            else
                                predicate.AddWithAnd(newPredicate);
                        }
                    }
                    return predicate;
                }
            }
            else if (filterNode.ElementCount > 0)
            {
                // convert elements to IPredicate
                var nodePredicate = BuildPredicateFromClauseNode(filterNode, inferredRelationsList);
                if(nodePredicate != null)
                    return nodePredicate;
            }
            return null;
        }

        /*
            -> eq (equals any) 
            -> neq (not equals any) 
            -> eqc (equals any, case insensitive [on a case sensitive database]) 
            -> neqc (not equals any, case insensitive [on a case sensitive database]) 
            -> in (same as eq) 
            -> nin (same as ne) 
            -> lk (like) 
            -> nlk (not like) 
            -> nl (null)
            -> nnl (not null)
            -> gt (greater than) 
            -> gte (greater than or equal to) 
            -> lt (less than) 
            -> lte (less than or equal to) 
            -> ct (full text contains) 
            -> ft (full text free text) 
            -> bt (between)
            -> nbt (not between)
         */
        private IPredicate BuildPredicateFromClauseNode(FilterNode filterNode, List<IEntityRelation> inferredRelationsList)
        {
            // there are always at least 2 elements
            if (filterNode.ElementCount < 2)
                return null;

            var elements = filterNode.Elements;

            //TODO: may need to mess with relation aliases and join types in the future
            var relations = new List<IEntityRelation>();
            var field = elements[0].IndexOf('.') == -1
                                      ? GetField(elements[0], true)
                                      : GetRelatedField(elements[0], relations);
            foreach(var relation in relations)
                inferredRelationsList.AddIfNotExists(relation);

            var comparisonOperatorStr = elements[1].ToLowerInvariant();

            var valueElements = elements.Skip(2).ToArray();
            var objElements = new object[valueElements.Length];

            Action<string> throwINEEx = (s) =>
                {
                    throw new ArgumentException(string.Format("Invalid number of elements in '{0}' filter clause", s));
                };

            string objAlias;
            IPredicate predicate;
            switch (comparisonOperatorStr)
            {
                case("bt"): //between
                case("nbt"): //not between
                    if (valueElements.Length < 2) throwINEEx(comparisonOperatorStr);
                    objElements[0] = ConvertStringToFieldValue(field, valueElements[0]);
                    objElements[1] = ConvertStringToFieldValue(field, valueElements[1]);
                    objAlias = valueElements.Length == 3 ? valueElements[2] : null;
                    predicate = new FieldBetweenPredicate(field, null, objElements[0], objElements[1], objAlias, comparisonOperatorStr == "nbt"); 
                    break;
                case ("in"): //same as eq
                case ("nin"): //same as ne
                case ("eq"): //equals any
                case ("neq"): //not equals any
                case ("eqc"): //equals any, case insensitive [on a case sensitive database] - only 1 element per clause with this option
                case ("neqc"): //not equals any, case insensitive [on a case sensitive database] - only 1 element per clause with this option
                    if (valueElements.Length < 1) throwINEEx(comparisonOperatorStr);
                    for(int i=0;i< valueElements.Length;i++)
                        objElements[i] = ConvertStringToFieldValue(field, valueElements[i]);
                    if (objElements.Length == 1 || comparisonOperatorStr == "eqci" || comparisonOperatorStr == "neci")
                    {
                        predicate = new FieldCompareValuePredicate(field, null, comparisonOperatorStr.StartsWith("n")
                                                                                    ? ComparisonOperator.NotEqual
                                                                                    : ComparisonOperator.Equal,
                                                                   objElements[0])
                            {
                                CaseSensitiveCollation =
                                    comparisonOperatorStr == "eqci" || comparisonOperatorStr == "neci"
                            };
                    }
                    else
                    {
                        if (comparisonOperatorStr.StartsWith("n"))
                            predicate = (EntityField2) field != objElements;
                        else
                            predicate = (EntityField2) field == objElements;
                    }
                    break;
                case("lk"): //like
                case("nlk"): //not like
                    if (valueElements.Length < 1) throwINEEx(comparisonOperatorStr);
                    objElements[0] = ConvertStringToFieldValue(field, valueElements[0].Replace('*', '%'));
                    objAlias = valueElements.Length == 2 ? valueElements[1] : null;
                    predicate = new FieldLikePredicate(field, null, objAlias, (string)objElements[0], comparisonOperatorStr == "nlk");
                    break;
                case("nl"): //null
                case("nnl"): //not null
                    predicate = new FieldCompareNullPredicate(field, null, null, comparisonOperatorStr == "nnl");
                    break;
                case("gt"): //greater than) 
                case("gte"): //greater than or equal to
                case("lt"): //less than
                case("lte"): //less than or equal to
                    if (valueElements.Length < 1) throwINEEx(comparisonOperatorStr);
                    objElements[0] = ConvertStringToFieldValue(field, valueElements[0]);
                    objAlias = valueElements.Length == 2 ? valueElements[1] : null;
                    var comparisonOperator = ComparisonOperator.GreaterThan;
                    if(comparisonOperatorStr == "gte") comparisonOperator = ComparisonOperator.GreaterEqual;
                    else if(comparisonOperatorStr == "lt") comparisonOperator = ComparisonOperator.LesserThan;
                    else if(comparisonOperatorStr == "lte") comparisonOperator = ComparisonOperator.LessEqual;
                    predicate = new FieldCompareValuePredicate(field, null, comparisonOperator, objElements[0], objAlias);
                    break;
                case("ct"): //full text contains
                case("ft"): //full text free text
                    if (valueElements.Length < 1) throwINEEx(comparisonOperatorStr);
                    objElements[0] = valueElements[0];
                    objAlias = valueElements.Length == 2 ? valueElements[1] : null;
                    predicate = new FieldFullTextSearchPredicate(field, null, comparisonOperatorStr == "ct" ? FullTextSearchOperator.Contains : FullTextSearchOperator.Freetext, (string)objElements[0], objAlias);
                    break;
                default:
                    return null;
            }
            return predicate;
        }

        private object ConvertStringToFieldValue(IEntityField2 field, string fieldValueStr)
        {
            var dataValueStr = fieldValueStr.Trim('\'', '\"');
            var dataType = field.DataType;
            if (dataType.IsGenericType && dataType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                if (string.IsNullOrEmpty(fieldValueStr))
                    return null; // it's ok to return null for nullable types

                dataType = dataType.GenericTypeArguments[0];
            }
            object dataValue = null;
            var cannotConvert = true;
            if (CanChangeType(fieldValueStr, dataType))
            {
                try
                {
                    dataValue = Convert.ChangeType(dataValueStr, dataType);
                    cannotConvert = false;
                }
                catch
                {
                }
            }
            if (cannotConvert)
                throw new ArgumentException(string.Format("Value '{0}' cannot be converted to type {1}.", dataValueStr, dataType));
            return dataValue;
        }

        private static bool CanChangeType(object value, Type conversionType)
        {
            if (conversionType == null)
            {
                return false;
            }

            if (value == null)
            {
                return false;
            }

            var convertible = value as IConvertible;
            if (convertible == null)
            {
                return false;
            }

            return true;
        }
        
        #endregion

        #endregion

        #region Static Helper Methods

        private static IDictionary<string, IEntityField2> GetEntityTypeFieldMap(EntityType entityType)
        {
            return EntityFieldsFactory.CreateEntityFieldsObject(entityType)
                                      .ToDictionary(k => k.Name, v => (IEntityField2) v);
        }

        private static IDictionary<string, IPrefetchPathElement2> GetEntityTypeIncludeMap(EntityType entityType)
        {
            return
                ((CommonEntityBase) GeneralEntityFactory.Create(entityType)).PrefetchPaths.ToDictionary(
                    k => k.PropertyName, v => v);
        }

        private static IDictionary<string, IEntityRelation> GetEntityTypeRelationMap(EntityType entityType)
        {
            return
                ((CommonEntityBase) GeneralEntityFactory.Create(entityType)).EntityRelations.ToDictionary(
                    k => k.MappedFieldName, v => v);
            ;
        }

        private static ExcludeIncludeFieldsList ConvertEntityTypeFieldNamesToExcludedIncludedFields(
            EntityType entityType, IEnumerable<string> fieldNames)
        {
            if (fieldNames == null)
                return null;

            var fieldMap = GetEntityTypeFieldMap(entityType);
            if (fieldMap == null)
                return null;

            var fields = new IncludeFieldsList();
            foreach (var fieldName in fieldNames)
            {
                var field = fieldMap.FirstOrDefault(f => f.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                if (!fields.Contains(field.Value))
                    fields.Add(field.Value);
            }
            return fields.Count > 0 ? fields : null;
        }

        #endregion
    }
}
