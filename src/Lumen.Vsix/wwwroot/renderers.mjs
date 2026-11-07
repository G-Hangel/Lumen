import { MarkdownIt, hljs, diffHtml } from './vendor/libraries.mjs';

export function isSafeLink(href) {
  if (typeof href !== 'string' || /[\s\u0000-\u001f\u007f]/.test(href)) return false;
  if (/^#[a-z][a-z0-9_-]*$/i.test(href)) return true;
  if (!/^(https?:\/\/|mailto:)/i.test(href)) return false;
  try {
    const url = new URL(href);
    return !url.username && !url.password && (url.protocol === 'mailto:' ? Boolean(url.pathname) : Boolean(url.hostname));
  } catch {
    return false;
  }
}

export function createMarkdownRenderer() {
  const markdown = new MarkdownIt({
    html: false,
    linkify: true,
    typographer: false,
    highlight(source, language) {
      if (['csharp', 'cs', 'c#'].includes(language.toLowerCase())) {
        return hljs.highlight(source, { language: 'csharp', ignoreIllegals: true }).value;
      }
      return '';
    },
  });
  markdown.validateLink = isSafeLink;
  markdown.renderer.rules.image = () => '';
  const linkOpen = markdown.renderer.rules.link_open || ((tokens, index, options, env, renderer) => renderer.renderToken(tokens, index, options));
  markdown.renderer.rules.link_open = (tokens, index, options, env, renderer) => {
    tokens[index].attrSet('rel', 'noopener noreferrer');
    return linkOpen(tokens, index, options, env, renderer);
  };
  return markdown;
}

const escapeHtml = value => String(value).replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
})[character]);

export function renderDiff(source, emptyLabel) {
  return diffHtml(source, {
    outputFormat: 'side-by-side',
    drawFileList: false,
    matching: 'lines',
    diffStyle: 'word',
    renderNothingWhenEmpty: false,
    // Render-only templates keep the runtime CSP free of unsafe-eval and remove the library's English controls.
    compiledTemplates: {
      'generic-file-path': { render: ({ fileDiffName }) => `<span class="d2h-file-name">${escapeHtml(fileDiffName)}</span>` },
      'generic-empty-diff': { render: () => `<tr><td>${escapeHtml(emptyLabel)}</td></tr>` },
    },
  });
}
