const fs = require('node:fs');

try {
	const formulas = JSON.parse(fs.readFileSync(0, 'utf8'));
	const katex = require('katex');
	const expectedVersion = require('../package.json').devDependencies.katex;
	if (katex.version !== expectedVersion) {
		throw new Error(`Expected KaTeX ${expectedVersion}, found ${katex.version}. Run npm install.`);
	}

	const html = formulas.map(formula => {
		try {
			return katex.renderToString(formula.tex, {
				displayMode: formula.displayMode,
				output: 'htmlAndMathml',
				throwOnError: true,
				trust: false,
			});
		} catch (error) {
			throw new Error(`Line ${formula.line}: ${error.message}`);
		}
	});

	process.stdout.write(JSON.stringify({
		stylesheet: `https://cdnjs.cloudflare.com/ajax/libs/KaTeX/${katex.version}/katex.min.css`,
		html,
	}));
} catch (error) {
	process.stderr.write(`${error.message}\n`);
	process.exitCode = 1;
}
