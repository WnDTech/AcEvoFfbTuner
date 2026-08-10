using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Help;

namespace AcEvoFfbTuner.Views.Pages;

public partial class HelpPage : UserControl
{
    private readonly List<HelpArticle> _allArticles = HelpCatalog.Articles.ToList();
    private string _filter = "";

    public HelpPage()
    {
        InitializeComponent();

        // Populate immediately so the topic list is never empty, regardless of
        // whether Loaded has fired yet (the page starts Visibility=Collapsed).
        RebuildArticleList();

        // Safety net: refresh whenever the page actually becomes visible.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                RebuildArticleList();
        };
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim().ToLowerInvariant();
        RebuildArticleList();
    }

    private void RebuildArticleList()
    {
        IEnumerable<HelpArticle> filtered = _allArticles;
        if (!string.IsNullOrEmpty(_filter))
            filtered = filtered.Where(a => a.SearchText.Contains(_filter));

        var list = filtered.ToList();

        ArticleList.ItemsSource = list;
        UpdateCount();

        if (list.Count > 0)
        {
            var current = ArticleList.SelectedItem as HelpArticle;
            if (current == null || !list.Contains(current))
                ArticleList.SelectedIndex = 0;
        }
    }

    private void UpdateCount()
    {
        var shown = ArticleList.Items.Count;
        CountText.Text = shown == _allArticles.Count
            ? $"{shown} topics — type to search"
            : $"{shown} of {_allArticles.Count} topics";
    }

    private void OnArticleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ArticleList.SelectedItem is HelpArticle article)
            RenderArticle(article);
    }

    private void RenderArticle(HelpArticle article)
    {
        ArticleTitle.Text = article.Title;
        ArticleSubtitle.Text = article.Subtitle;
        SectionList.ItemsSource = article.Sections;

        ContentScroller.ScrollToTop();
    }
}
