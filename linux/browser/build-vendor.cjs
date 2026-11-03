'use strict';
const fs=require('node:fs'),path=require('node:path'),ts=require('typescript');
const root=path.join(__dirname,'vendor/browser-extension-compat'),output=path.join(__dirname,'vendor/generated');
fs.mkdirSync(path.join(output,'extensions'),{recursive:true});
for(const area of ['extensions','browser-automation']){
fs.mkdirSync(path.join(output,area),{recursive:true});
for(const file of fs.readdirSync(path.join(root,area))){
 if(!file.endsWith('.ts'))continue;
 const result=ts.transpileModule(fs.readFileSync(path.join(root,area,file),'utf8'),{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.CommonJS,esModuleInterop:true},fileName:file,reportDiagnostics:true});
 if(result.diagnostics?.some(d=>d.category===ts.DiagnosticCategory.Error))throw Error('Extension source compilation failed: '+file);
 fs.writeFileSync(path.join(output,area,file.replace(/\.ts$/,'.js')),result.outputText);
}
}
fs.mkdirSync(path.join(output,'browser-webviews'),{recursive:true});
fs.writeFileSync(path.join(output,'browser-webviews/index.js'),"module.exports=require('../../../extension_registry.cjs');\n");
console.log('Compiled reviewed browser extension compatibility source.');
