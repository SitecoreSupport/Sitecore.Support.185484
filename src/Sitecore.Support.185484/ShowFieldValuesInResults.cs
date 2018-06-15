namespace Sitecore.Buckets.Pipelines.UI.FillItem
{
    using Sitecore;
    using Sitecore.Buckets.Pipelines.UI.FillItem.FieldTypeRenderers;
    using Sitecore.ContentSearch.SearchTypes;
    using Sitecore.Data;
    using Sitecore.Data.Fields;
    using Sitecore.Data.Items;
    using Sitecore.Globalization;
    using Sitecore.Resources;
    using Sitecore.Web.UI;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Web;

    public class ShowFieldValuesInResults : FillItemProcessor
    {
        [Obsolete("Use ProcessFieldsToShow instead.")]
        protected virtual void GetQuickActions(FillItemArgs args)
        {
            this.ProcessFieldsToShow(args);
        }

        private static void GetValue(IEnumerable<TemplateFieldItem> templateFieldItems, Item innerItem, SitecoreUISearchResultItem searchResultItem)
        {
            foreach (TemplateFieldItem item in templateFieldItems.Distinct<TemplateFieldItem>(new TemplateFieldComparaer()))
            {
                Field field = innerItem.Fields[item.Name];
                if (field != null)
                {
                    IFieldTypeRenderer renderer = FieldTypeRendererFactory.GetRenderer(field);
                    searchResultItem.Content = searchResultItem.Content + renderer.GetContent();
                    if (((field.TypeKey != "multilist") || string.IsNullOrEmpty(searchResultItem.ImagePath)) && ((field.TypeKey != "attachment") || string.IsNullOrEmpty(searchResultItem.ImagePath)))
                    {
                        string imagePath = renderer.GetImagePath();
                        if (!string.IsNullOrEmpty(imagePath))
                        {
                            searchResultItem.ImagePath = imagePath;
                        }
                    }
                }
            }
            if (searchResultItem.Content == null)
            {
                searchResultItem.Content = string.Empty;
            }
            if (string.IsNullOrEmpty(searchResultItem.ImagePath))
            {
                searchResultItem.ImagePath = HttpUtility.HtmlEncode(Images.GetThemedImageSource(searchResultItem.GetItem().Appearance.Icon, ImageDimension.id48x48));
            }
        }

        public override void Process(FillItemArgs args)
        {
            if (args != null)
            {
                this.ProcessFieldsToShow(args);
            }
        }

        protected virtual void ProcessFieldsToShow(FillItemArgs args)
        {
            List<TemplateFieldItem> templateFields = args.TemplateFields;
            foreach (SitecoreUISearchResultItem item in args.ResultItems.OfType<SitecoreUISearchResultItem>())
            {
                Language language;
                item.Content = null;
                Language.TryParse(item.Language, out language);
                Item innerItem = Context.ContentDatabase.GetItem(item.ItemId, language, new Sitecore.Data.Version(item.Version));
                if (innerItem == null)
                {
                    innerItem = Context.Database.GetItem(item.ItemId, language, new Sitecore.Data.Version(item.Version));
                }
                if (innerItem != null)
                {
                    GetValue(templateFields, innerItem, item);
                }
            }
        }
    }
}

