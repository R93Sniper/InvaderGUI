# Invader GUI
Invader GUI is a Visual Wrapper for SnowyMouse's Halo Combat Evolved Toolkit Invader. 

**This project is not officially associated or affiliated with SnowyMouse or Invader, nor is it endorsed by them.**</br>
This project exists explicitly to provide an easier time for those who are either not comfortable with Invader's CLI interface or simply do not want to interact with it.</br>
You are **_required_** to have an install of Invader for this wrapper to work. This does not recreate its functionality, just provides an easier way to use it.</br>
This was built against Invader version `0.55.0.r4134.33518109`, which is the latest version as of time of creation.


Features
----------------------------------------------------------------------------------------------
This UI provides access to every one of Invader's tools minus `invader-edit`. There is access to `invader-edit-qt` however, so the functionality is still there.</br>
</br>
Related tools have been grouped together as follows: </br>
Build Map - `invader-build` and `invader-resource` </br>
Asset Interchange - `invader-bitmap`, `invader-font`, `invader-sound`, `invader-string`, `invader-model`, and `invader-lightmap` </br>
Tag Handling - `invader-bludgeon`, `invader-strip`, `invader-convert`, `invader-dependency`, `invader-collection`, `invader-extract`, and `invader-recover` </br>
Map Tools - `invader-info`, `invader-crc`, and `invader-index` </br>
The remaining tools (`invader-archive`, `invader-compile`, `invader-compare`, and `invader-refactor`) are their own tabs.</br>


`invader-refactor` has its own unique window to allow you to build a full stack of refactor commands and execute them. </br>
There is a preview of the current and planned directories, as well as a full execution stack that can be reordered at will.</br>
You can save and load stacks of commands as `.txt`, `.bat`, and `.json` files.</br>
The UI provides drag and drop behavior in the planned window. You can move and rename files through that window for ease of use.


AI Disclosure
----------------------------------------------------------------------------------------------
A significant portion of this work was created using Codex. The code has been reviewed by a human (me) and I've also written parts of the code myself by hand. </br>
AI was simply used as a means to speed the process up and build up some familiarity with a WPF workflow, as it is new to me.


Credits
----------------------------------------------------------------------------------------------
**SnowyMouse** - Creator of the original tool that this wraps, Invader.</br>
**Num-005** - Osoyoos, whose visual style heavily inspired the layout of this tool.</br> 
**R93_Sniper** - Creating the tool and code review of Codex's output.
