// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Search.ashx.cs" company="Sitecore">
//   Copyright (c) Sitecore. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace ItemBuckets.Services
{
  using System;
  using System.Collections;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.Linq;
  using System.Text;
  using System.Threading.Tasks;
  using System.Web;
  using System.Web.SessionState;
  using Newtonsoft.Json;
  using Sitecore;
  using Sitecore.Buckets.Caching;
  using Sitecore.Buckets.Extensions;
  using Sitecore.Buckets.Pipelines.Search.GetFacets;
  using Sitecore.Buckets.Pipelines.UI.FetchContextData;
  using Sitecore.Buckets.Pipelines.UI.FetchContextView;
  using Sitecore.Buckets.Pipelines.UI.FillItem;
  using Sitecore.Buckets.Pipelines.UI.Search;
  using Sitecore.Buckets.Search;
  using Sitecore.Buckets.Util;
  using Sitecore.Caching;
  using Sitecore.Configuration;
  using Sitecore.ContentSearch;
  using Sitecore.ContentSearch.Diagnostics;
  using Sitecore.ContentSearch.Exceptions;
  using Sitecore.ContentSearch.Linq;
  using Sitecore.ContentSearch.Linq.Parsing;
  using Sitecore.ContentSearch.SearchTypes;
  using Sitecore.ContentSearch.Utilities;
  using Sitecore.Data;
  using Sitecore.Data.Fields;
  using Sitecore.Data.Items;
  using Sitecore.Globalization;
  using Constants = Sitecore.Buckets.Util.Constants;
  using EnumerableExtensions = Sitecore.Buckets.Extensions.EnumerableExtensions;
  using Version = Sitecore.Data.Version;
  using Sitecore.ContentSearch.Linq;
  /// <summary>
  /// Search End Point
  /// </summary>
  [UsedImplicitly]
  public class Search : SearchHttpTaskAsyncHandler, IRequiresSessionState
  {
    #region Fields

    // TODO: Search HttpHandler has shared fields - The handler cannot be reused by multiple requests

    /// <summary>
    /// The cache hashtable
    /// </summary>
    private static volatile Hashtable cacheHashtable;

    /// <summary>
    /// The this lock
    /// </summary>
    private static readonly object ThisLock = new object();

    #endregion

    #region Public Properties

    /// <summary>Gets a value indicating whether is reusable.</summary>
    public override bool IsReusable
    {
      get
      {
        return false;
      }
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the cache hash table.
    /// </summary>
    /// <value>
    /// The cache hash table.
    /// </value>
    private static Hashtable CacheHashTable
    {
      get
      {
        if (cacheHashtable == null)
        {
          lock (ThisLock)
          {
            if (cacheHashtable == null)
            {
              cacheHashtable = new Hashtable();
            }
          }
        }

        return cacheHashtable;
      }
    }

    #endregion

    #region Public Methods and Operators

    /// <summary>The process request.</summary>
    /// <param name="context">The context.</param>
    public override void ProcessRequest(HttpContext context)
    {
    }

    #endregion

    /// <summary>
    /// The process request async.
    /// </summary>
    /// <param name="context">
    /// The context.
    /// </param>
    /// <returns>
    /// The <see cref="Task"/>.
    /// </returns>
    public override async Task ProcessRequestAsync(HttpContext context)
    {
      if (!ContentSearchManager.Locator.GetInstance<IContentSearchConfigurationSettings>().ItemBucketsEnabled())
      {
        return;
      }

      context.Response.ContentType = "application/json";
      context.Response.ContentEncoding = Encoding.UTF8;
      this.Stopwatch = new Stopwatch();
      this.ItemsPerPage = BucketConfigurationSettings.DefaultNumberOfResultsPerPage;

      this.ExtractSearchQuery(context.Request.QueryString);
      this.ExtractSearchQuery(context.Request.Form);

      this.CheckSecurity();

      if (this.AbortSearch)
      {
        return;
      }

      var debugMode = MainUtil.GetBool(SearchHelper.GetDebug(this.SearchQuery), false);
      if (debugMode)
      {
        this.SearchQuery.RemoveAll(x => x.Type == "debug");
        if (!BucketConfigurationSettings.EnableBucketDebug)
        {
          Constants.EnableTemporaryBucketDebug = true;
        }
      }

      try
      {
        this.PerformSearch(context);
      }
      finally
      {
        if (debugMode)
        {
          Constants.EnableTemporaryBucketDebug = false;
        }
      }
    }

    private void PerformSearch(HttpContext context)
    {
      if (this.RunFacet)
      {
        try
        {
          var facets = GetFacetsPipeline.Run(new GetFacetsArgs(this.SearchQuery, this.LocationFilter));
          var fullSearch = new FullSearch
          {
            PageNumbers = 1,
            facets = facets,
            SearchCount = "1",
            CurrentPage = 1
          };

          var facetCallback = this.FormatCallbackString(fullSearch);

          context.Response.Write(facetCallback);
        }
        catch (IndexNotFoundException)
        {
          var fullSearch = new FullSearch
          {
            PageNumbers = 0,
            facets = new List<IEnumerable<SitecoreUIFacet>>(),
            SearchCount = "0",
            CurrentPage = 0
          };

          var facetCallback = this.FormatCallbackString(fullSearch);

          context.Response.Write(facetCallback);
        }

        return;
      }

      var db = this.Database.IsNullOrEmpty() ? Context.ContentDatabase : Factory.GetDatabase(this.Database);

      this.StoreUserContextSearches();

      SitecoreIndexableItem startLocationItem = db.GetItem(this.LocationFilter);

      ISearchIndex searchIndex;
      try
      {
        searchIndex = this.IndexName.IsEmpty() ? ContentSearchManager.GetIndex(startLocationItem) : ContentSearchManager.GetIndex(this.IndexName);
      }
      catch (IndexNotFoundException)
      {
        SearchLog.Log.Warn("No index found for " + startLocationItem.Item.ID);
        var fullSearch = new FullSearch
        {
          PageNumbers = 0,
          items = new List<UISearchResult>(),
          launchType = GetEditorLaunchType(),
          SearchTime = this.SearchTime,
          SearchCount = "0",
          ContextData = new List<Tuple<View, object>>(),
          ContextDataView = new List<Tuple<int, View, string, IEnumerable<UISearchResult>>>(),
          CurrentPage = 0,
          Location = Context.ContentDatabase.GetItem(this.LocationFilter) != null ? Context.ContentDatabase.GetItem(this.LocationFilter).Name : Translate.Text(Sitecore.Buckets.Localization.Texts.CurrentItem),
          ErrorMessage = Translate.Text(Sitecore.Buckets.Localization.Texts.NoIndexesFound)
        };
        var callbackString = this.FormatCallbackString(fullSearch);
        context.Response.Write(callbackString);
        return;
      }

      using (var contextForSearch = searchIndex.CreateSearchContext())
      {
        IEnumerable<UISearchResult> items;
        int itemsCount;
        int currentPage = int.Parse(this.PageNumber);
        for (; ; )
        {
          var searchArgs = new UISearchArgs(contextForSearch, this.SearchQuery, startLocationItem)
          {
            Page = currentPage - 1,
            PageSize = this.ItemsPerPage
          };


          this.Stopwatch.Start();
          var query = UISearchPipeline.Run(searchArgs);
          var results = query.GetResults();

          items = results.Hits.Select(h => h.Document);

          if (BucketConfigurationSettings.EnableBucketDebug || Constants.EnableTemporaryBucketDebug)
          {
            SearchLog.Log.Info(string.Format("Search Query : {0}", ((IHasNativeQuery)query).Query));
            SearchLog.Log.Info(string.Format("Search Index : {0}", searchIndex.Name));
          }

          itemsCount = results.TotalSearchResults;

          if (itemsCount != 0 || currentPage == 1)
          {
            break;
          }

          currentPage = 1;
        }

        var enumerableCollextion = items.ToList();

        int pageNumbers = itemsCount % this.ItemsPerPage == 0
                        ? Math.Max(itemsCount / this.ItemsPerPage, 1)
                        : itemsCount / this.ItemsPerPage + 1;

        var startItemIdx = (currentPage - 1) * this.ItemsPerPage;

        if (startItemIdx >= itemsCount)
        {
          currentPage = 1;
        }

        var showFieldsQuick = new List<TemplateFieldItem>();

        enumerableCollextion = this.ProcessCachedItems(items, startLocationItem, showFieldsQuick, enumerableCollextion);

        if (this.IndexName == string.Empty)
        {
          enumerableCollextion =
              EnumerableExtensions.RemoveWhere(
                  enumerableCollextion, item => item.Name == null || item.Content == null).ToList();
        }

        if (!BucketConfigurationSettings.SecuredItems.Equals("hide", StringComparison.InvariantCultureIgnoreCase))
        {
          if (itemsCount > BucketConfigurationSettings.DefaultNumberOfResultsPerPage &&
              enumerableCollextion.Count < BucketConfigurationSettings.DefaultNumberOfResultsPerPage &&
              currentPage <= pageNumbers)
          {
            while (enumerableCollextion.Count < BucketConfigurationSettings.DefaultNumberOfResultsPerPage)
            {
              enumerableCollextion.Add(new UISearchResult()
              {
                ItemId = Guid.NewGuid().ToString()
              });
            }
          }
          else if (enumerableCollextion.Count < itemsCount && currentPage == 1)
          {
            while (enumerableCollextion.Count < itemsCount &&
                   itemsCount < BucketConfigurationSettings.DefaultNumberOfResultsPerPage)
            {
              enumerableCollextion.Add(new UISearchResult()
              {
                ItemId = Guid.NewGuid().ToString()
              });
            }
          }
        }

        this.Stopwatch.Stop();
        var contextDataPipeline = FetchContextDataPipeline.Run(new FetchContextDataArgs(this.SearchQuery, contextForSearch, startLocationItem));

        var contextDataViewsPipeline = FetchContextViewPipeline.Run(new FetchContextViewArgs(this.SearchQuery, contextForSearch, startLocationItem, showFieldsQuick));

        var fullSearch = new FullSearch
        {
          PageNumbers = pageNumbers,
          items = enumerableCollextion,
          launchType = GetEditorLaunchType(),
          SearchTime = this.SearchTime,
          SearchCount = itemsCount.ToString(),
          ContextData = contextDataPipeline,
          ContextDataView = contextDataViewsPipeline,
          CurrentPage = currentPage,
          Location = Context.ContentDatabase.GetItem(this.LocationFilter) != null ? Context.ContentDatabase.GetItem(this.LocationFilter).Name : Translate.Text(Sitecore.Buckets.Localization.Texts.CurrentItem)
        };

        var callbackString = this.FormatCallbackString(fullSearch);

        context.Response.Write(callbackString);

        if (BucketConfigurationSettings.EnableBucketDebug || Constants.EnableTemporaryBucketDebug)
        {
          SearchLog.Log.Info("Search Took : " + this.Stopwatch.ElapsedMilliseconds + "ms");
        }
      }
    }

    private string FormatCallbackString(FullSearch fullSearch)
    {
      return this.Callback + "(" + JsonConvert.SerializeObject(fullSearch) + ")";
    }

    private List<UISearchResult> ProcessCachedItems(IEnumerable<UISearchResult> items, SitecoreIndexableItem startLocationItem, List<TemplateFieldItem> showFieldsQuick, List<UISearchResult> enumerableCollextion)
    {
      if (items == null)
      {
        return enumerableCollextion;
      }

      if (Context.ContentDatabase == null)
      {
        return enumerableCollextion;
      }

      ISearchIndex searchIndex;

      try
      {
        var contextIndexName = ContentSearchManager.GetContextIndexName((SitecoreIndexableItem)Context.ContentDatabase.GetItem(ItemIDs.TemplateRoot));
        if (contextIndexName != null)
        {
          searchIndex = ContentSearchManager.GetIndex((SitecoreIndexableItem)Context.ContentDatabase.GetItem(ItemIDs.TemplateRoot));
        }
        else
        {
          return this.FillSearchResults(showFieldsQuick, enumerableCollextion);
        }
      }
      catch (IndexNotFoundException)
      {
        SearchLog.Log.Warn("No index found for " + ItemIDs.TemplateRoot);
        return enumerableCollextion;
      }

      using (var searchContext = searchIndex.CreateSearchContext())
      {
        IEnumerable<Tuple<string, string, string>> cachedIsDisplayedSearch = ProcessCachedDisplayedSearch(startLocationItem, searchContext);

        var itemCache = CacheManager.GetItemCache(Context.ContentDatabase);

        foreach (var templateFieldItem in cachedIsDisplayedSearch)
        {
          Language language;
          Sitecore.Globalization.Language.TryParse(templateFieldItem.Item2, out language);

          var cachedItem = itemCache.GetItem(new ID(templateFieldItem.Item1), language, new Version(templateFieldItem.Item3));

          if (cachedItem == null)
          {
            cachedItem = Context.ContentDatabase.GetItem(new ID(templateFieldItem.Item1), language, new Version(templateFieldItem.Item3));
            if (cachedItem != null)
            {
              CacheManager.GetItemCache(Context.ContentDatabase).AddItem(cachedItem.ID, language, cachedItem.Version, cachedItem);
            }
          }

          if (cachedItem == null)
          {
            continue;
          }

          if (showFieldsQuick.Contains(FieldTypeManager.GetTemplateFieldItem(new Field(cachedItem.ID, cachedItem))))
          {
            continue;
          }

          showFieldsQuick.Add(FieldTypeManager.GetTemplateFieldItem(new Field(cachedItem.ID, cachedItem)));
        }
      }

      return this.FillSearchResults(showFieldsQuick, enumerableCollextion);
    }

    private List<UISearchResult> FillSearchResults(List<TemplateFieldItem> showFieldsQuick, List<UISearchResult> enumerableCollextion)
    {
      return FillItemPipeline.Run(new FillItemArgs(showFieldsQuick, enumerableCollextion, this.Language));
    }

    /// <summary>
    /// Processes the cached displayed search.
    /// </summary>
    /// <param name="startLocationItem">The start location item.</param>
    /// <param name="searchContext">The search context.</param>
    /// <returns></returns>
    private static IEnumerable<Tuple<string, string, string>> ProcessCachedDisplayedSearch(SitecoreIndexableItem startLocationItem, IProviderSearchContext searchContext)
    {
      string cacheName = string.Concat("IsDisplayedInSearchResults", "[", Context.ContentDatabase.Name, "]");

      ICache cache = (ICache)CacheHashTable[cacheName];
      var cachedIsDisplayedSearch = cache != null ? cache.GetValue("cachedIsDisplayedSearch") as IEnumerable<Tuple<string, string, string>> : null;

      if (cachedIsDisplayedSearch == null)
      {
        CultureInfo cultureInfo = startLocationItem != null ? startLocationItem.Culture : new CultureInfo(Settings.DefaultLanguage);

        var templateSearch = searchContext.GetQueryable<SitecoreUISearchResultItem>(new CultureExecutionContext(cultureInfo))
          .Where(templateField => templateField["Is Displayed in Search Results".ToLowerInvariant()] == "1");

        cachedIsDisplayedSearch = templateSearch.ToList().ConvertAll(d => new Tuple<string, string, string>(d.GetItem().ID.ToString(), d.Language, d.Version));

        if (CacheHashTable[cacheName] == null)
        {
          lock (CacheHashTable.SyncRoot)
          {
            if (CacheHashTable[cacheName] == null)
            {
              List<ID> changedFieldIDsOfTemplate = new List<ID>();
              changedFieldIDsOfTemplate.Add(new ID(Constants.IsDisplayedInSearchResults));
              cache = new DisplayedInSearchResultsCache(cacheName, changedFieldIDsOfTemplate);
              cacheHashtable[cacheName] = cache;
            }
          }
        }

        cache.Add("cachedIsDisplayedSearch", cachedIsDisplayedSearch);
      }

      return cachedIsDisplayedSearch;
    }
  }
}