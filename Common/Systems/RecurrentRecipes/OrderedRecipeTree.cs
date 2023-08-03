﻿using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace MagicStorage.Common.Systems.RecurrentRecipes {
	public sealed class OrderedRecipeTree {
		private readonly List<OrderedRecipeTree> leaves = new();
		public readonly OrderedRecipeContext context;

		public IReadOnlyList<OrderedRecipeTree> Leaves => leaves;

		public OrderedRecipeTree Root { get; private set; }

		public OrderedRecipeTree(OrderedRecipeContext context) {
			this.context = context;
		}

		public void Add(OrderedRecipeTree tree) {
			leaves.Add(tree);
			tree.Root = this;
		}

		public void AddRange(IEnumerable<OrderedRecipeTree> trees) {
			foreach (var tree in trees) {
				leaves.Add(tree);
				tree.Root = this;
			}
		}

		public void Clear() {
			context.amountToCraft = 0;
			leaves.Clear();
		}

		public Stack<OrderedRecipeContext> GetProcessingOrder() {
			Stack<OrderedRecipeContext> recipeStack = new();
			Queue<OrderedRecipeTree> treeQueue = new();
			treeQueue.Enqueue(this);

			while (treeQueue.TryDequeue(out OrderedRecipeTree branch)) {
				recipeStack.Push(branch.context);

				foreach (var leaf in branch.Leaves)
					treeQueue.Enqueue(leaf);
			}

			return recipeStack;
		}

		/// <summary>
		/// Trims the branches of any trees whose ingredient requirement is met.<br/>
		/// Use <see cref="GetProcessingOrder"/> to get the updated order of the recipe contexts.
		/// </summary>
		/// <param name="getItemCountForIngredient">A function taking the recipe, the ingredient type and returning how many items can satisfy that ingredient requirement</param>
		public void TrimBranches(Func<Recipe, int, int> getItemCountForIngredient) {
			if (!CraftingGUI.disableNetPrintingForIsAvailable)
				NetHelper.Report(true, "Trimming branches of recipe tree...");

			// Go from the top of the tree down, cutting off any branches when necessary
			Queue<OrderedRecipeTree> queue = new();
			foreach (var leaf in leaves)
				queue.Enqueue(leaf);

			int depth;
			while (queue.TryDequeue(out OrderedRecipeTree branch)) {
				// Check if the amount needed has been satisfied
				// If it is, this recipe and its children are not needed
				Recipe recipe = branch.context.recipe;

				int result = recipe.createItem.type;
				ref int remaining = ref branch.context.amountToCraft;

				remaining -= getItemCountForIngredient(recipe, result);

				depth = branch.context.depth;
				if (remaining <= 0) {
					if (!CraftingGUI.disableNetPrintingForIsAvailable)
						NetHelper.Report(false, $"Branch trimmed: Depth = {depth}, Recipe result = {recipe.createItem.stack} {Lang.GetItemNameValue(result)}");

					branch.Clear();
				} else {
					// Check the leaves
					foreach (var leaf in branch.Leaves)
						queue.Enqueue(leaf);
				}
			}
		}

		/// <summary>
		/// Returns an enumeration of every recipe used in this recursion tree
		/// </summary>
		public IEnumerable<Recipe> GetAllRecipes() {
			Queue<OrderedRecipeTree> treeQueue = new();
			HashSet<Recipe> usedRecipes = new HashSet<Recipe>(ReferenceEqualityComparer.Instance);
			treeQueue.Enqueue(this);

			while (treeQueue.TryDequeue(out OrderedRecipeTree branch)) {
				Recipe recipe = branch.context.recipe;

				if (usedRecipes.Add(recipe))
					yield return recipe;

				foreach (var leaf in branch.Leaves)
					treeQueue.Enqueue(leaf);
			}
		}

		public IEnumerable<int> GetRequiredTiles() {
			return GetAllRecipes().SelectMany(static r => r.requiredTile).Distinct();
		}

		public bool HasCondition(Recipe.Condition condition) {
			return GetAllRecipes().Any(r => r.HasCondition(condition));
		}

		public List<RequiredMaterialInfo> GetRequiredMaterials(out List<ItemInfo> excessResults) {
			// Get the info for one craft, then multiply the contents by how many batches would be needed
			var recipeStack = GetProcessingOrder();

			Dictionary<int, int> itemIndices = new();
			Dictionary<int, int> groupIndices = new();
			List<RequiredMaterialInfo> materials = new();

			excessResults = new();
			Dictionary<int, int> excessIndicies = new();

			EnvironmentSandbox sandbox;
			IEnumerable<EnvironmentModule> modules;
			if (CraftingGUI.requestingAmountFromUI) {
				var heart = CraftingGUI.GetHeart();

				sandbox = new EnvironmentSandbox(Main.LocalPlayer, heart);
				modules = heart.GetModules();
			} else {
				sandbox = default;
				modules = Array.Empty<EnvironmentModule>();
			}

			// NOTE: [ThreadStatic] only runs the field initializer on one thread
			CraftingGUI.DroppedItems ??= new();

			foreach (OrderedRecipeContext context in recipeStack) {
				// Trimmed branch?  Ignore
				if (context.amountToCraft <= 0)
					continue;

				int ingredientBatches = (int)Math.Ceiling(context.amountToCraft / (double)context.recipe.createItem.stack);

				Recipe recipe = context.recipe;
				foreach (Item item in recipe.requiredItem) {
					// Consume from the excess results first
					int stack = item.stack * ingredientBatches;

					if (excessIndicies.TryGetValue(item.type, out int excessIndex)) {
						ItemInfo info = excessResults[excessIndex];

						if (info.stack >= stack) {
							excessResults[excessIndex] = info.UpdateStack(-stack);
							stack = 0;
							continue;
						} else {
							stack -= info.stack;
							excessResults[excessIndex] = info.SetStack(0);
						}
					}

					bool usedRecipeGroup = false;
					foreach (int groupID in recipe.acceptedGroups) {
						RecipeGroup group = RecipeGroup.recipeGroups[groupID];
						if (group.ContainsItem(item.type)) {
							// Consume from the excess results first
							foreach (int groupItem in group.ValidItems) {
								if (excessIndicies.TryGetValue(groupItem, out excessIndex)) {
									ItemInfo info = excessResults[excessIndex];

									if (info.stack >= stack) {
										excessResults[excessIndex] = info.UpdateStack(-stack);
										stack = 0;
										continue;
									} else {
										stack -= info.stack;
										excessResults[excessIndex] = info.SetStack(0);
									}

									usedRecipeGroup = true;
									
									if (stack <= 0)
										goto checkNonGroup;
								}
							}

							usedRecipeGroup = true;
							
							// Check if the recipe group has already been used
							if (!groupIndices.TryGetValue(groupID, out int index)) {
								groupIndices[groupID] = materials.Count;
								materials.Add(RequiredMaterialInfo.FromGroup(group, stack));
							} else
								materials[index] = materials[index].UpdateStack(stack);

							break;
						}
					}

					checkNonGroup:

					if (!usedRecipeGroup) {
						if (!itemIndices.TryGetValue(item.type, out int index)) {
							itemIndices[item.type] = materials.Count;
							materials.Add(RequiredMaterialInfo.FromItem(item.type, stack));
						} else
							materials[index] = materials[index].UpdateStack(stack);
					}
				}

				// Fake a craft
				CraftingGUI.CatchDroppedItems = true;
				CraftingGUI.DroppedItems.Clear();

				Item createItem = recipe.createItem.Clone();

				for (int i = 0; i < ingredientBatches; i++) {
					RecipeLoader.OnCraft(createItem, recipe, recipe.requiredItem);

					foreach (EnvironmentModule module in modules)
						module.OnConsumeItemsForRecipe(sandbox, recipe, recipe.requiredItem);
				}

				CraftingGUI.CatchDroppedItems = false;

				// Add the result item and any dropped items to the excess list
				createItem.stack *= ingredientBatches;

				CraftingGUI.DroppedItems.Insert(0, createItem);

				foreach (Item item in CraftingGUI.DroppedItems) {
					if (!excessIndicies.TryGetValue(item.type, out int itemIndex)) {
						excessIndicies[item.type] = excessResults.Count;
						excessResults.Add(new ItemInfo(item));
					} else
						excessResults[itemIndex] = excessResults[itemIndex].UpdateStack(item.stack);
				}
			}

			return materials;
		}
	}
}
