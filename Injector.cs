﻿/* 
InjectModuleInitializer

MSBuild task and command line program to inject a module initializer
into a .NET assembly

Copyright (C) 2009-2012 Einar Egilsson
http://einaregilsson.com/2009/12/16/module-initializers-in-csharp/

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using Mono.Collections.Generic;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace EinarEgilsson.Utilities.InjectModuleInitializer
{
    public class InjectionException : Exception
    {
        public InjectionException(string msg) : base(msg) { }
    }

    internal class Injector
    {
        public const string DefaultInitializerClassName = "ModuleInitializer";
        public const string DefaultInitializerMethodName = "Run";

        static Injector()
        {
            AppDomain.CurrentDomain.AssemblyResolve += LoadEmbeddedAssembly;
        }

        static Assembly LoadEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            
            string name = new AssemblyName(args.Name).Name;
            string resourceName = typeof(Injector).Namespace + ".lib." + name + ".dll";
            Stream stream = typeof(Injector).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null) {
                return null;
            }
            using (stream) {
                byte[] buf = new byte[stream.Length];
                stream.Read(buf,0, buf.Length);
                return System.Reflection.Assembly.Load(buf);
            }
        }


        private AssemblyDefinition Assembly { get; set; }

        private string PdbFile(string assemblyFile)
        {
            Debug.Assert(assemblyFile != null);
            string path = Path.ChangeExtension(assemblyFile, ".pdb");
            if (File.Exists(path))
            {
                return path;
            }
            return null;
        }


        public void Inject(string assemblyFile, string moduleInitializer=null, string keyfile=null)
        {
            try
            {
                if (!File.Exists(assemblyFile))
                {
                    throw new InjectionException(Errors.AssemblyDoesNotExist(assemblyFile));
                }
                if (keyfile != null && !File.Exists(keyfile))
                {
                    throw new InjectionException(Errors.KeyFileDoesNotExist(keyfile));
                }
                ReadAssembly(assemblyFile);
                MethodReference callee = GetCalleeMethod(moduleInitializer);
                InjectInitializer(callee);

                WriteAssembly(assemblyFile, keyfile);
            }
            catch (Exception ex)
            {
                throw new InjectionException(ex.Message);
            }
        }

        private void InjectInitializer(MethodReference callee)
        {
            Debug.Assert(Assembly != null);
            TypeReference voidRef = Assembly.MainModule.Import(callee.ReturnType);
            const MethodAttributes attributes = MethodAttributes.Static
                                                | MethodAttributes.SpecialName
                                                | MethodAttributes.RTSpecialName;
            var cctor = new MethodDefinition(".cctor", attributes, voidRef);
            ILProcessor il = cctor.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Call, callee));
            il.Append(il.Create(OpCodes.Ret));

            TypeDefinition moduleClass = Find(Assembly.MainModule.Types, t => t.Name == "<Module>");
            moduleClass.Methods.Add(cctor);

            Debug.Assert(moduleClass != null, "Found no module class!");
        }

        private void WriteAssembly(string assemblyFile, string keyfile)
        {
            Debug.Assert(Assembly != null);
            var writeParams = new WriterParameters();
            if (PdbFile(assemblyFile) != null)
            {
                writeParams.WriteSymbols = true;
                writeParams.SymbolWriterProvider = new PdbWriterProvider();
            }
            if (keyfile != null)
            {
                writeParams.StrongNameKeyPair = new StrongNameKeyPair(File.ReadAllBytes(keyfile));
            }
            Assembly.Write(assemblyFile, writeParams);
        }

        private void ReadAssembly(string assemblyFile)
        {
            Debug.Assert(Assembly == null);
            var readParams = new ReaderParameters(ReadingMode.Immediate);
            if (PdbFile(assemblyFile) != null)
            {
                readParams.ReadSymbols = true;
                readParams.SymbolReaderProvider = new PdbReaderProvider();
            }
            Assembly = AssemblyDefinition.ReadAssembly(assemblyFile, readParams);
        }

        private MethodReference GetCalleeMethod(string moduleInitializer)
        {
            Debug.Assert(Assembly != null);
            ModuleDefinition module = Assembly.MainModule;
            string methodName;
            TypeDefinition moduleInitializerClass;
            if (string.IsNullOrEmpty(moduleInitializer))
            {
                methodName = DefaultInitializerMethodName;
                moduleInitializerClass = Find(module.Types, t => t.Name == DefaultInitializerClassName);
                if (moduleInitializerClass == null)
                {
                    throw new InjectionException(Errors.NoModuleInitializerTypeFound());
                }
            }
            else
            {
                if (!moduleInitializer.Contains("::"))
                {
                    throw new InjectionException(Errors.InvalidFormatForModuleInitializer());
                }
                string typeName = moduleInitializer.Substring(0, moduleInitializer.IndexOf("::"));
                methodName = moduleInitializer.Substring(typeName.Length + 2);
                moduleInitializerClass = Find(module.Types, t => t.FullName == typeName);
                if (moduleInitializerClass == null)
                {
                    throw new InjectionException(Errors.TypeNameDoesNotExist(typeName));
                }
            }

            MethodDefinition callee = Find(moduleInitializerClass.Methods, m => m.Name == methodName);
            if (callee == null)
            {
                throw new InjectionException(Errors.MethodNameDoesNotExist(moduleInitializerClass.FullName, methodName));
            }
            if (callee.Parameters.Count > 0)
            {
                throw new InjectionException(Errors.ModuleInitializerMayNotHaveParameters());
            }
            if (callee.IsPrivate || callee.IsFamily)
            {
                throw new InjectionException(Errors.ModuleInitializerMayNotBePrivate());
            }
            if (!callee.ReturnType.FullName.Equals(typeof(void).FullName)) //Don't compare the types themselves, they might be from different CLR versions.
            {
                throw new InjectionException(Errors.ModuleInitializerMustBeVoid());
            }
            if (!callee.IsStatic)
            {
                throw new InjectionException(Errors.ModuleInitializerMustBeStatic());
            }
            return callee;
        }

        //No LINQ, since we want to target 2.0
        private static T Find<T>(Collection<T> objects, Predicate<T> condition) where T:class
        {
            foreach (T obj in objects)
            {
                if (condition(obj))
                {
                    return obj;
                }
            }
            return null;
        }
    }
}
