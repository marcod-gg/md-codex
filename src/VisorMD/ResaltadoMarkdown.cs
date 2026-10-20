using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace VisorMD;

// Resaltado de sintaxis Markdown para el editor, con los colores del tema activo.
public static class ResaltadoMarkdown
{
    public static IHighlightingDefinition Crear(Paleta p)
    {
        var xshd = $$"""
            <SyntaxDefinition name="Markdown" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Titulo" foreground="{{p.Titulo}}" fontWeight="bold" />
              <Color name="Fuerte" fontWeight="bold" />
              <Color name="Enfasis" fontStyle="italic" />
              <Color name="Codigo" foreground="{{p.Codigo}}" />
              <Color name="Cita" foreground="{{p.Cita}}" fontStyle="italic" />
              <Color name="Enlace" foreground="{{p.Enlace}}" />
              <Color name="Lista" foreground="{{p.Lista}}" fontWeight="bold" />
              <Color name="Tenue" foreground="{{p.Tenue}}" />
              <RuleSet>
                <Span color="Codigo" multiline="true">
                  <Begin>^\s*(```|~~~)</Begin>
                  <End>^\s*(```|~~~)</End>
                </Span>
                <Span color="Tenue" multiline="true">
                  <Begin>&lt;!--</Begin>
                  <End>--&gt;</End>
                </Span>
                <Rule color="Titulo">^\#{1,6}\s.*$</Rule>
                <Rule color="Cita">^\s*&gt;.*$</Rule>
                <Rule color="Tenue">^\s*((-\s*){3,}|(\*\s*){3,}|(_\s*){3,})$</Rule>
                <Rule color="Lista">^\s*([-*+]|\d+[.)])\s+(\[[ xX]\]\s)?</Rule>
                <Rule color="Codigo">`[^`\n]+`</Rule>
                <Rule color="Fuerte">\*\*[^*\n]+\*\*|__[^_\n]+__</Rule>
                <Rule color="Enfasis">(?&lt;![*\w])\*[^*\s][^*\n]*\*|(?&lt;!\w)_[^_\s][^_\n]*_(?!\w)</Rule>
                <Rule color="Enlace">!?\[[^\]\n]*\]\([^)\n]*\)|&lt;https?://[^&gt;\s]+&gt;</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        using var lector = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(lector, HighlightingManager.Instance);
    }
}
