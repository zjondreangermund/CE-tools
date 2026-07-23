# CE Tools Ribbon Architecture

The CE Tools ribbon is organised by engineering workflow rather than by source-file name. Each public module remains implemented in a separate C# source file, while the ribbon presents compact category panels and split-button/flyout menus.

## Project

- CE Project Setup
- Coordinate Systems
- Standards Selection
- Templates
- Project Wizard

Project Setup fields:

- Project Name
- Client
- Country
- Town
- Coordinate System
- Standards
- Drawing Template
- Units

## Survey

- CE Survey Tools

## Drawings

- CE Drawing Cleanup Tools
- CE Drawing Tools

## Geometry

- CE Feature Line Tools
- CE Alignment Tools
- CE Profile Tools
- CE Assembly Tools
- CE Corridor Tools
- CE Surface Tools

## Site Design

- CE Parking Tools
- CE Landscaping Tools
- CE Grading Tools
- CE Earthworks Tools

## Utilities

- CE Utility Tools
- CE Pipe Network Tools
- CE Drainage Tools

## Standards

- CE Design Standards Tools
- CE Typical Details Tools

## Analysis

- CE Quantity Tools
- CE Report Tools
- CE Simulation Tools
- CE Traffic Analysis Tools

## Production

- CE Plan Production Tools

## BIM / Visualization

- CE Visualization Tools
- CE As-Built Tools

Visualization integrations may later include Twinmotion, InfraWorks, Plex-Earth and related platforms. Integrations must respect each product's licensing and supported APIs.

## Management

- CE QA/QC Tools
- CE Document Manager

## Help

- Help
- Tutorials
- Updates
- License Manager
- Feedback
- About CE Tools

## Future CE AI Tools

- AI Parking Designer
- AI Road Designer
- AI Drawing Review
- AI Report Generator
- AI Standards Assistant
- AI Quantity Checker

## Ribbon behaviour

1. Each category is a Civil 3D ribbon panel.
2. Related modules are presented as split buttons or flyout buttons.
3. The main button face starts the default or most-used workflow.
4. The arrow opens related subcommands.
5. Every direct `CE_` command remains available at the command line.
6. Empty future modules are not shown as working commands until source exists.
7. The layout must remain usable at common Civil 3D ribbon widths.
8. Ribbon grouping does not combine source modules; implementation remains modular.

## Current transition

The current alpha still uses several temporary individual panels and buttons. The full category/flyout conversion is a release-gate task before the next combined public test build. New modules should be assigned to the architecture above even while the temporary alpha ribbon remains in use.
